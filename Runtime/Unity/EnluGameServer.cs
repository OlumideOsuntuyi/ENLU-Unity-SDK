#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Enlu
{
    /// <summary>
    /// The game server SDK (GSDK) for dedicated servers hosted on ENLU fleets, with the same lifecycle
    /// as PlayFab's GSDK:
    /// <list type="number">
    /// <item><see cref="Start"/> as early as possible (state <c>Initializing</c>, heartbeats every second).</item>
    /// <item>Load your map, open your listen socket on <see cref="GetContainerPort"/>, then call
    /// <see cref="ReadyForPlayers"/> (state <c>StandingBy</c>).</item>
    /// <item>When a matchmaker allocates the server, <see cref="OnAllocated"/> fires on the main thread
    /// (or <see cref="WaitForAllocationAsync"/> completes) with the session id, metadata and expected players.</item>
    /// <item>Report players with <see cref="UpdateConnectedPlayers"/> as they join and leave.</item>
    /// <item>When the match ends, call <see cref="Shutdown"/> (the process exits; the fleet replaces it with a
    /// fresh standby server). If the platform asks first, <see cref="OnShutdown"/> fires and the process quits.</item>
    /// </list>
    /// Without <c>ENLU_GSDK_URL</c> (Editor, local runs) the SDK runs in <b>local mode</b>: no network,
    /// and the server is "allocated" to a local session about two seconds after <see cref="ReadyForPlayers"/>.
    /// </summary>
    public static class EnluGameServer
    {
        private static EnluGsdkAgent? _agent;
        private static GsdkConfig? _config;
        private static volatile bool _healthy = true;
        private static long _lastPumpTicks;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static float _nextHealthAt;
        private static float _nextLocalTickAt;
        private static Action? _ticker;
        private static bool _quitting;

        /// <summary>Raised on the main thread when the server is allocated to a session.</summary>
        public static event Action<SessionInfo>? OnAllocated;

        /// <summary>
        /// Raised on the main thread when the platform asks the server to shut down. Save what you need
        /// quickly; afterwards the process quits (see <see cref="AutoQuitOnShutdown"/>).
        /// </summary>
        public static event Action? OnShutdown;

        /// <summary>
        /// Health callbacks, evaluated on the main thread about once per second. Return false to report
        /// <c>Unhealthy</c>; the fleet replaces servers that stay unhealthy. All handlers must return true.
        /// </summary>
        public static event Func<bool>? OnHealthCheck;

        /// <summary>Quit the process after <see cref="OnShutdown"/> handlers ran (default true). Ignored in the Editor.</summary>
        public static bool AutoQuitOnShutdown { get; set; } = true;

        /// <summary>
        /// Report <c>Unhealthy</c> when the main thread has not run a frame for this long (default 30 s),
        /// so a frozen server gets replaced. 0 disables the check.
        /// </summary>
        public static float MainThreadStallSeconds { get; set; } = 30;

        /// <summary>True after <see cref="Start"/>.</summary>
        public static bool IsStarted => _agent != null;

        /// <summary>True when no node agent is configured (Editor or local run).</summary>
        public static bool IsLocalMode => Config.IsLocal;

        /// <summary>Current lifecycle state.</summary>
        public static GameServerState State => _agent?.State ?? GameServerState.Initializing;

        /// <summary>The allocated session, or null.</summary>
        public static SessionInfo? Session => _agent?.Session;

        /// <summary>Players last reported with <see cref="UpdateConnectedPlayers"/>.</summary>
        public static IReadOnlyList<string> ConnectedPlayers => _agent?.ConnectedPlayers ?? Array.Empty<string>();

        /// <summary>Everything the node agent passed in environment variables.</summary>
        public static GsdkConfig Config => _config ??= GsdkConfig.FromEnvironment();

        /// <summary>Instance id (<c>ENLU_INSTANCE_ID</c>).</summary>
        public static string InstanceId => Config.InstanceId;
        /// <summary>App id (<c>ENLU_APP_ID</c>).</summary>
        public static string AppId => Config.AppId;
        /// <summary>Workload name (<c>ENLU_WORKLOAD</c>).</summary>
        public static string Workload => Config.Workload;
        /// <summary>Region (<c>ENLU_REGION</c>).</summary>
        public static string Region => Config.Region;
        /// <summary>Public address players connect to (<c>ENLU_PUBLIC_ADDRESS</c>).</summary>
        public static string PublicAddress => Config.PublicAddress;

        /// <summary>Public port players connect to for a named port (e.g. <c>"game"</c>), or <paramref name="fallback"/>.</summary>
        public static int GetPublicPort(string name, int fallback = 0) => Config.GetPublicPort(name, fallback);

        /// <summary>Port to listen on inside the container for a named port, or <paramref name="fallback"/> (local mode).</summary>
        public static int GetContainerPort(string name, int fallback = 0) => Config.GetContainerPort(name, fallback);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _agent?.Stop();
            _agent = null;
            _config = null;
            _ticker = null;
            _quitting = false;
            _healthy = true;
            OnAllocated = null;
            OnShutdown = null;
            OnHealthCheck = null;
        }

        /// <summary>
        /// Starts the GSDK: reads the environment and begins heartbeating in state <c>Initializing</c>.
        /// Call once, on the main thread, as early as possible (e.g. in your bootstrap scene's Awake).
        /// </summary>
        /// <param name="localMetadata">Local mode only: metadata of the simulated session.</param>
        public static void Start(IDictionary<string, string>? localMetadata = null)
        {
            if (_agent != null) return;
            _config = GsdkConfig.FromEnvironment();
            var runner = EnluRunner.Instance;
            Interlocked.Exchange(ref _lastPumpTicks, Clock.ElapsedTicks);

            var agent = new EnluGsdkAgent(_config, null, EnluRunner.Post)
            {
                HealthCheck = ReadHealth,
            };
            if (localMetadata != null) foreach (var kv in localMetadata) agent.LocalMetadata[kv.Key] = kv.Value;
            agent.Allocated += RaiseAllocated;
            agent.ShutdownRequested += RaiseShutdown;
            _agent = agent;

            _ticker = Pump;
            runner.AddTicker(_ticker);
            Application.quitting += OnQuitting;

#if UNITY_WEBGL && !UNITY_EDITOR
            EnluLog.Warn("EnluGameServer runs in local mode on WebGL: game servers do not run in browsers.");
#else
            if (!_config.IsLocal)
            {
                EnluLog.Info("Game server SDK started: " + _config);
                agent.StartBackgroundLoop();
                return;
            }
#endif
            // Local mode: no network and no threads; the main thread drives the simulated agent.
            EnluLog.Info("Game server SDK in local mode (ENLU_GSDK_URL is not set). The server will be allocated to a local session after ReadyForPlayers().");
            _nextLocalTickAt = 0;
        }

        /// <summary>
        /// Marks the server ready: state becomes <c>StandingBy</c> and the fleet may allocate it.
        /// Non-blocking; <see cref="OnAllocated"/> fires (and <see cref="WaitForAllocationAsync"/> completes) when it is.
        /// </summary>
        public static void ReadyForPlayers()
        {
            RequireStarted().ReadyForPlayers();
        }

        /// <summary>
        /// Completes on the main thread when the server is allocated (immediately if it already is).
        /// Cancelled if the platform shuts the server down first.
        /// </summary>
        public static Task<SessionInfo> WaitForAllocationAsync(CancellationToken cancellationToken = default) =>
            RequireStarted().WaitForAllocationAsync(cancellationToken);

        /// <summary>Reports the connected players' ids (sent with every heartbeat). Call on join and leave.</summary>
        public static void UpdateConnectedPlayers(IEnumerable<string> playerIds) =>
            RequireStarted().UpdateConnectedPlayers(playerIds);

        /// <summary>
        /// Ends this server at the end of a match: reports <c>Terminating</c> and quits the process with
        /// <paramref name="exitCode"/> (0 = normal end). In the Editor it only stops the SDK.
        /// </summary>
        public static void Shutdown(int exitCode = 0)
        {
            var agent = _agent;
            if (agent != null)
            {
                agent.SendFinalHeartbeat(TimeSpan.FromSeconds(2));
                Teardown();
            }
            Quit(exitCode);
        }

        private static EnluGsdkAgent RequireStarted() =>
            _agent ?? throw new InvalidOperationException("Call EnluGameServer.Start() first.");

        private static bool ReadHealth()
        {
            if (!_healthy) return false;
            var stall = MainThreadStallSeconds;
            if (stall <= 0) return true;
            var since = (Clock.ElapsedTicks - Interlocked.Read(ref _lastPumpTicks)) / (double)Stopwatch.Frequency;
            return since < stall;
        }

        private static void Pump()
        {
            Interlocked.Exchange(ref _lastPumpTicks, Clock.ElapsedTicks);
            var now = Time.realtimeSinceStartup;
            if (now >= _nextHealthAt)
            {
                _nextHealthAt = now + 1f;
                _healthy = EvaluateHealth();
            }
            var agent = _agent;
            if (agent != null && agent.IsLocalMode && now >= _nextLocalTickAt)
            {
                _nextLocalTickAt = now + (float)agent.Interval.TotalSeconds;
                _ = agent.TickAsync(); // completes synchronously in local mode
            }
        }

        private static bool EvaluateHealth()
        {
            var handlers = OnHealthCheck;
            if (handlers == null) return true;
            foreach (var d in handlers.GetInvocationList())
            {
                try
                {
                    if (!((Func<bool>)d)()) return false;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                    return false;
                }
            }
            return true;
        }

        private static void RaiseAllocated(SessionInfo session)
        {
            try
            {
                OnAllocated?.Invoke(session);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        private static void RaiseShutdown()
        {
            try
            {
                OnShutdown?.Invoke();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
            if (AutoQuitOnShutdown)
            {
                Teardown();
                Quit(0);
            }
        }

        private static void OnQuitting()
        {
            if (_quitting) return;
            _quitting = true;
            var agent = _agent;
            if (agent == null) return;
            // Let the agent know right away instead of waiting for heartbeats to time out.
            agent.SendFinalHeartbeat(TimeSpan.FromSeconds(1));
            Teardown();
        }

        private static void Teardown()
        {
            var agent = _agent;
            _agent = null;
            agent?.Stop();
            if (_ticker != null)
            {
                var runner = EnluRunner.Current;
                if (runner != null) runner.RemoveTicker(_ticker);
                _ticker = null;
            }
            Application.quitting -= OnQuitting;
        }

        private static void Quit(int exitCode)
        {
#if UNITY_EDITOR
            EnluLog.Info("Game server finished (exit code " + exitCode + "). In the Editor the process keeps running.");
#else
            _quitting = true;
            Application.Quit(exitCode);
#endif
        }
    }
}
