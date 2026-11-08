#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu
{
    /// <summary>
    /// The engine-independent core of the game server SDK: tracks state, sends heartbeats to the
    /// node agent and turns its answers into allocation and shutdown callbacks.
    /// Most games use the static <c>EnluGameServer</c> facade instead, which runs this on a background
    /// thread and raises its events on Unity's main thread.
    /// </summary>
    public sealed class EnluGsdkAgent : IDisposable
    {
        /// <summary>Sends one heartbeat and returns the agent's answer. Throw on failure.</summary>
        public delegate Task<GsdkHeartbeatResponse> HeartbeatSender(GsdkHeartbeatRequest request, CancellationToken cancellationToken);

        private readonly object _lock = new object();
        private readonly HeartbeatSender _sender;
        private readonly Action<Action> _dispatch;
        private readonly TaskCompletionSource<SessionInfo> _allocatedTcs =
            new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _players = new List<string>();

        private GameServerState _state = GameServerState.Initializing;
        private SessionInfo? _session;
        private bool _allocatedRaised;
        private bool _shutdownRaised;
        private int _consecutiveFailures;
        private CancellationTokenSource? _loopCts;
        private Task? _loop;
        private int _localHeartbeatsInStandby;

        /// <summary>Environment the agent gave this process.</summary>
        public GsdkConfig Config { get; }

        /// <summary>True when no node agent is configured and the agent is simulated.</summary>
        public bool IsLocalMode { get; }

        /// <summary>Time between heartbeats (default 1 s).</summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Local mode: allocate automatically shortly after <see cref="ReadyForPlayers"/> (default true).</summary>
        public bool LocalAutoAllocate { get; set; } = true;

        /// <summary>Local mode: metadata of the simulated session.</summary>
        public Dictionary<string, string> LocalMetadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Called before each heartbeat (on the heartbeat thread) to report health. Null means healthy.</summary>
        public Func<bool>? HealthCheck { get; set; }

        /// <summary>Raised (through the dispatcher) once, when the server is allocated.</summary>
        public event Action<SessionInfo>? Allocated;

        /// <summary>Raised (through the dispatcher) once, when the agent asks the server to shut down.</summary>
        public event Action? ShutdownRequested;

        /// <summary>Current state.</summary>
        public GameServerState State
        {
            get { lock (_lock) return _state; }
        }

        /// <summary>The allocated session, or null.</summary>
        public SessionInfo? Session
        {
            get { lock (_lock) return _session; }
        }

        /// <summary>Connected players as last reported.</summary>
        public IReadOnlyList<string> ConnectedPlayers
        {
            get { lock (_lock) return _players.ToArray(); }
        }

        /// <summary>Consecutive failed heartbeats (0 when the last one succeeded).</summary>
        public int ConsecutiveFailures
        {
            get { lock (_lock) return _consecutiveFailures; }
        }

        /// <summary>
        /// Creates the agent client.
        /// </summary>
        /// <param name="config">Environment from the node agent.</param>
        /// <param name="sender">Heartbeat transport; null uses HTTP to <c>ENLU_GSDK_URL</c>, or the local simulator when it is unset.</param>
        /// <param name="dispatcher">Runs event handlers (e.g. on the main thread); null runs them inline on the heartbeat thread.</param>
        public EnluGsdkAgent(GsdkConfig config, HeartbeatSender? sender = null, Action<Action>? dispatcher = null)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            IsLocalMode = sender == null && config.IsLocal;
            _sender = sender ?? (config.IsLocal ? LocalSender : CreateHttpSender(config));
            _dispatch = dispatcher ?? (a => a());
        }

        /* ------------------------------ lifecycle ------------------------------ */

        /// <summary>Starts heartbeating on a background thread (state Initializing).</summary>
        public void StartBackgroundLoop()
        {
            lock (_lock)
            {
                if (_loop != null) return;
                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                _loop = Task.Run(() => LoopAsync(token));
            }
        }

        /// <summary>The server finished loading: switch to StandingBy so it can be allocated.</summary>
        public void ReadyForPlayers()
        {
            lock (_lock)
            {
                if (_state == GameServerState.Initializing) _state = GameServerState.StandingBy;
            }
        }

        /// <summary>Completes when the server is allocated (immediately if it already is).</summary>
        public Task<SessionInfo> WaitForAllocationAsync(CancellationToken cancellationToken = default)
        {
            if (!cancellationToken.CanBeCanceled) return _allocatedTcs.Task;
            return WaitWithCancellation(_allocatedTcs.Task, cancellationToken);
        }

        private static async Task<SessionInfo> WaitWithCancellation(Task<SessionInfo> task, CancellationToken ct)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                var done = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                if (done != task) throw new OperationCanceledException(ct);
                return await task.ConfigureAwait(false);
            }
        }

        /// <summary>Reports the connected players (sent with every heartbeat).</summary>
        public void UpdateConnectedPlayers(IEnumerable<string> playerIds)
        {
            if (playerIds == null) throw new ArgumentNullException(nameof(playerIds));
            lock (_lock)
            {
                _players.Clear();
                foreach (var p in playerIds) if (!string.IsNullOrEmpty(p)) _players.Add(p);
            }
        }

        /// <summary>Stops the heartbeat loop (does not send anything).</summary>
        public void Stop()
        {
            CancellationTokenSource? cts;
            lock (_lock)
            {
                cts = _loopCts;
                _loopCts = null;
                _loop = null;
            }
            if (cts == null) return;
            try
            {
                cts.Cancel();
            }
            finally
            {
                cts.Dispose();
            }
        }

        /// <summary>
        /// Switches to Terminating, stops the loop and sends one last heartbeat so the agent knows the
        /// server is going away. Returns false if it could not be delivered within <paramref name="timeout"/>.
        /// </summary>
        public bool SendFinalHeartbeat(TimeSpan timeout)
        {
            lock (_lock) _state = GameServerState.Terminating;
            Stop();
            if (IsLocalMode) return true;
            try
            {
                using (var cts = new CancellationTokenSource(timeout))
                {
                    var t = Task.Run(() => _sender(BuildRequest(), cts.Token));
                    return t.Wait(timeout) && t.Status == TaskStatus.RanToCompletion;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public void Dispose() => Stop();

        /* ------------------------------ heartbeats ------------------------------ */

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                bool stop;
                lock (_lock) stop = _shutdownRaised;
                if (stop) break;
                try
                {
                    await Task.Delay(Interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private GsdkHeartbeatRequest BuildRequest()
        {
            var healthy = true;
            var check = HealthCheck;
            if (check != null)
            {
                try
                {
                    healthy = check();
                }
                catch (Exception e)
                {
                    healthy = false;
                    EnluLog.Error("Health check threw: " + e.Message);
                }
            }
            lock (_lock)
            {
                return new GsdkHeartbeatRequest { State = _state, Players = new List<string>(_players), Healthy = healthy };
            }
        }

        /// <summary>
        /// Sends one heartbeat and applies the answer. Never throws for transport errors (they are
        /// counted and logged); returns the response, or null when the heartbeat failed.
        /// </summary>
        public async Task<GsdkHeartbeatResponse?> TickAsync(CancellationToken cancellationToken = default)
        {
            var request = BuildRequest();
            GsdkHeartbeatResponse response;
            try
            {
                response = await _sender(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                int failures;
                lock (_lock) failures = ++_consecutiveFailures;
                if (e is EnluException ee && (ee.HttpStatus == 401 || ee.HttpStatus == 403))
                {
                    if (failures == 1 || failures % 30 == 0)
                        EnluLog.Error("The node agent rejected the GSDK token (HTTP " + ee.HttpStatus.ToString(CultureInfo.InvariantCulture) + "). Is ENLU_GSDK_TOKEN passed through unchanged?");
                }
                else if (failures == 1 || failures % 10 == 0)
                {
                    EnluLog.Warn("Heartbeat failed (" + failures.ToString(CultureInfo.InvariantCulture) + " in a row): " + e.Message);
                }
                return null;
            }
            lock (_lock) _consecutiveFailures = 0;
            Apply(response);
            return response;
        }

        private void Apply(GsdkHeartbeatResponse response)
        {
            SessionInfo? allocated = null;
            var shutdown = false;
            lock (_lock)
            {
                if (response.Operation == GsdkOperation.Active && !_allocatedRaised && _state != GameServerState.Terminating)
                {
                    _allocatedRaised = true;
                    _state = GameServerState.Active;
                    _session = new SessionInfo(response.SessionId ?? string.Empty, response.SessionMetadata, response.InitialPlayers);
                    allocated = _session;
                }
                if (response.Operation == GsdkOperation.Terminate && !_shutdownRaised)
                {
                    _shutdownRaised = true;
                    _state = GameServerState.Terminating;
                    shutdown = true;
                }
            }
            if (allocated != null)
            {
                EnluLog.Info("Allocated to session " + allocated.SessionId + ".");
                _allocatedTcs.TrySetResult(allocated);
                var session = allocated;
                _dispatch(() => Allocated?.Invoke(session));
            }
            if (shutdown)
            {
                EnluLog.Info("The node agent asked this server to shut down.");
                _allocatedTcs.TrySetCanceled();
                _dispatch(() => ShutdownRequested?.Invoke());
            }
        }

        /* ------------------------------- senders ------------------------------- */

        private Task<GsdkHeartbeatResponse> LocalSender(GsdkHeartbeatRequest request, CancellationToken ct)
        {
            var r = new GsdkHeartbeatResponse { Operation = GsdkOperation.Continue };
            lock (_lock)
            {
                if (request.State == GameServerState.StandingBy && LocalAutoAllocate)
                {
                    // Allocate on the second heartbeat in StandingBy, like a matchmaker picking it up.
                    if (++_localHeartbeatsInStandby >= 2)
                    {
                        r.Operation = GsdkOperation.Active;
                        r.SessionId = "local-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                        foreach (var kv in LocalMetadata) r.SessionMetadata[kv.Key] = kv.Value;
                    }
                }
                else if (request.State == GameServerState.Active && _session != null)
                {
                    r.Operation = GsdkOperation.Active;
                    r.SessionId = _session.SessionId;
                }
            }
            return Task.FromResult(r);
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private static System.Net.Http.HttpClient? _httpClient;

        /// <summary>Creates the HTTP heartbeat sender for <c>POST {ENLU_GSDK_URL}/gsdk/v1/heartbeat</c>.</summary>
        public static HeartbeatSender CreateHttpSender(GsdkConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var url = config.GsdkUrl + "/gsdk/v1/heartbeat";
            var token = config.Token ?? string.Empty;
            var http = _httpClient ??= new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return async (request, ct) =>
            {
                using (var msg = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url))
                {
                    msg.Headers.TryAddWithoutValidation("X-Enlu-Gsdk-Token", token);
                    msg.Content = new System.Net.Http.StringContent(request.ToJson(), System.Text.Encoding.UTF8, "application/json");
                    using (var res = await http.SendAsync(msg, ct).ConfigureAwait(false))
                    {
                        var body = res.Content == null ? string.Empty : await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var status = (int)res.StatusCode;
                        if (status < 200 || status >= 300) throw EnluException.FromResponse(status, body, null);
                        return GsdkHeartbeatResponse.Parse(body);
                    }
                }
            };
        }
#else
        /// <summary>Not available on WebGL: game servers do not run in browsers.</summary>
        public static HeartbeatSender CreateHttpSender(GsdkConfig config) =>
            (request, ct) => throw new PlatformNotSupportedException("The game server SDK does not run on WebGL.");
#endif
    }
}
