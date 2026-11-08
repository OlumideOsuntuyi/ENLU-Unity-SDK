#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu
{
    /// <summary>
    /// The server-side ENLU API (<c>/v1/apps/:app/…</c>) for <b>dedicated game servers and trusted
    /// backends only</b>: allocate match servers, write player data and statistics the client may not
    /// touch, write server events and run cloud functions on a player's behalf.
    /// </summary>
    /// <remarks>
    /// <para><b>Security: never ship an API key in a client build.</b> Anything inside a game client can
    /// be extracted, and an API key gives full server access to your app. Use this class only in a
    /// dedicated server build (e.g. <c>UNITY_SERVER</c>) or a backend you control, and pass the key
    /// through the <c>ENLU_API_KEY</c> environment variable (e.g. an ENLU secret mapped into the
    /// workload's env), not through a serialized field or asset.</para>
    /// <para>Give the key only the scopes it needs (for a game server: <c>allocations</c>, <c>data</c>,
    /// <c>stats</c>, <c>events</c>, <c>functions</c>).</para>
    /// </remarks>
    public sealed partial class EnluServerApi
    {
        /// <summary>Environment variable read for the API key.</summary>
        public const string ApiKeyEnv = "ENLU_API_KEY";
        /// <summary>Environment variable read for the control node URL when none is passed.</summary>
        public const string ApiUrlEnv = "ENLU_API_URL";
        /// <summary>Environment variable read for the app when none is passed (set by the node agent).</summary>
        public const string AppIdEnv = "ENLU_APP_ID";

        private readonly EnluHttpPipeline _http;
        private readonly Dictionary<string, string> _auth;

        /// <summary>Route builder.</summary>
        public EnluServerRoutes Routes { get; }

        /// <summary>App id or name.</summary>
        public string App { get; }

        /// <summary>
        /// Creates the server API with explicit plumbing.
        /// </summary>
        /// <param name="options">Base URL and app; empty values fall back to <c>ENLU_API_URL</c> and <c>ENLU_APP_ID</c>.</param>
        /// <param name="apiKey">An <c>enlu_key_…</c> API key; null reads <c>ENLU_API_KEY</c>.</param>
        /// <param name="transport">Sends requests (UnityWebRequest in Unity, or <c>EnluHttpClientTransport</c> off the main thread).</param>
        /// <param name="scheduler">Waits between retries.</param>
        public EnluServerApi(EnluClientOptions options, string? apiKey, IEnluTransport transport, IEnluScheduler scheduler)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) || options.BaseUrl.Trim() == "https://"
                ? Environment.GetEnvironmentVariable(ApiUrlEnv) ?? string.Empty
                : options.BaseUrl;
            var app = string.IsNullOrWhiteSpace(options.App) ? Environment.GetEnvironmentVariable(AppIdEnv) ?? string.Empty : options.App;
            var key = apiKey ?? Environment.GetEnvironmentVariable(ApiKeyEnv);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("No API key: set the " + ApiKeyEnv + " environment variable on the server (never ship a key in a client build).");
            key = key!.Trim();
            if (!key.StartsWith("enlu_key_", StringComparison.Ordinal) && !key.StartsWith("enlu_op_", StringComparison.Ordinal))
                throw new ArgumentException("That does not look like an ENLU API key (enlu_key_…).", nameof(apiKey));

            Routes = new EnluServerRoutes(baseUrl, app);
            App = app.Trim();
            _auth = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key };
            _http = new EnluHttpPipeline(transport, scheduler, new EnluRetryPolicy { MaxRetries = Math.Max(0, options.MaxRetries) }, options.TimeoutSeconds)
            {
                LogRequests = options.LogRequests,
            };
        }

        private Task<object?> Call(string method, string url, object? body, EnluRetryMode mode, CancellationToken ct) =>
            _http.SendAsync(method, url, body, _auth, mode, ct);

        /* ----------------------------- allocations ----------------------------- */

        /// <summary>
        /// Takes a standby game server for a session (matchmaker flow). Idempotent per session id: pass
        /// your match id as <see cref="AllocationRequest.SessionId"/> and retries return the same server.
        /// Throws <c>unavailable</c> (503) when no standby server is free after the retries.
        /// </summary>
        public async Task<SessionAllocation> AllocateAsync(AllocationRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            // With a session id the call is idempotent, so network errors may be retried too.
            var mode = string.IsNullOrEmpty(request.SessionId) ? EnluRetryMode.RejectedOrUnavailable : EnluRetryMode.Idempotent;
            return SessionAllocation.FromJson(await Call("POST", Routes.Allocations(), request.ToJsonObject(), mode, cancellationToken));
        }

        /// <summary>Allocates a server for <paramref name="workload"/> with optional session id, regions, players and metadata.</summary>
        public Task<SessionAllocation> AllocateAsync(string workload, string? sessionId = null, IEnumerable<string>? regions = null,
            IEnumerable<string>? players = null, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        {
            var r = new AllocationRequest { Workload = workload, SessionId = sessionId };
            if (regions != null) r.Regions.AddRange(regions);
            if (players != null) r.Players.AddRange(players);
            if (metadata != null) foreach (var kv in metadata) r.Metadata[kv.Key] = kv.Value;
            return AllocateAsync(r, cancellationToken);
        }

        /// <summary>Looks up an active session's server.</summary>
        public async Task<SessionAllocation> GetAllocationAsync(string sessionId, CancellationToken cancellationToken = default) =>
            SessionAllocation.FromJson(await Call("GET", Routes.Allocation(sessionId), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>Lists all active (allocated) sessions.</summary>
        public async Task<List<SessionAllocation>> ListAllocationsAsync(CancellationToken cancellationToken = default)
        {
            var node = await Call("GET", Routes.Allocations(), null, EnluRetryMode.Idempotent, cancellationToken);
            var list = new List<SessionAllocation>();
            var arr = EnluJson.GetArray(EnluJson.AsObject(node), "sessions");
            if (arr != null) foreach (var s in arr) list.Add(SessionAllocation.FromJson(s));
            return list;
        }

        /// <summary>Ends a session: the control node stops its server.</summary>
        public Task EndSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Call("DELETE", Routes.Allocation(sessionId), null, EnluRetryMode.RejectedOnly, cancellationToken);

        /* -------------------------------- players ------------------------------- */

        /// <summary>Reads a player's profile (including statistics).</summary>
        public async Task<PlayerProfile> GetPlayerAsync(string playerId, CancellationToken cancellationToken = default) =>
            PlayerProfile.FromJson(await Call("GET", Routes.Player(playerId), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>
        /// Writes player data with server authority. <see cref="DataAccess.ReadOnly"/> keys (currency,
        /// unlocks) can be read but not changed by the client; <see cref="DataAccess.Internal"/> keys are
        /// invisible to it. A null value deletes a key. Returns the number of keys written.
        /// </summary>
        public async Task<int> SetPlayerDataAsync(string playerId, IDictionary<string, object?> data, DataAccess access = DataAccess.ReadOnly,
            DataVisibility visibility = DataVisibility.Private, CancellationToken cancellationToken = default)
        {
            EnluClient.ValidateData(data);
            var body = new Dictionary<string, object?>
            {
                ["data"] = data,
                ["access"] = EnluEnumText.ToApi(access),
                ["visibility"] = EnluEnumText.ToApi(visibility),
            };
            var node = await Call("PUT", Routes.PlayerData(playerId), body, EnluRetryMode.Idempotent, cancellationToken);
            return EnluJson.GetInt(EnluJson.AsObject(node), "updated");
        }

        /// <summary>
        /// Updates a player's statistics (any statistic; undefined ones are created). Returns the stored
        /// values after aggregation.
        /// </summary>
        public async Task<Dictionary<string, double>> UpdatePlayerStatisticsAsync(string playerId, IDictionary<string, double> statistics,
            CancellationToken cancellationToken = default)
        {
            if (statistics == null || statistics.Count == 0) throw new ArgumentException("Pass at least one statistic.", nameof(statistics));
            foreach (var kv in statistics)
            {
                if (!EnluIds.IsValidName(kv.Key)) throw new ArgumentException("Invalid statistic name: " + kv.Key, nameof(statistics));
                if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value)) throw new ArgumentException("Statistic values must be finite: " + kv.Key, nameof(statistics));
            }
            var node = await Call("POST", Routes.PlayerStatistics(playerId), new Dictionary<string, object?> { ["statistics"] = statistics },
                EnluRetryMode.RejectedOnly, cancellationToken);
            return EnluClient.ReadNumberMap(EnluJson.GetObject(EnluJson.AsObject(node), "statistics"));
        }

        /// <summary>Reads a leaderboard, optionally centred on a player and for an older version.</summary>
        public async Task<Leaderboard> GetLeaderboardAsync(string name, int top = 10, string? aroundPlayerId = null, int? version = null,
            CancellationToken cancellationToken = default)
        {
            top = Math.Max(1, Math.Min(100, top));
            return Leaderboard.FromJson(await Call("GET", Routes.Leaderboard(name, top, aroundPlayerId, version), null, EnluRetryMode.Idempotent, cancellationToken));
        }

        /// <summary>Reads all title data including internal keys.</summary>
        public async Task<Dictionary<string, object?>> GetTitleDataAsync(CancellationToken cancellationToken = default)
        {
            var node = await Call("GET", Routes.TitleData(), null, EnluRetryMode.Idempotent, cancellationToken);
            return EnluJson.GetObject(EnluJson.AsObject(node), "data") ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        /* --------------------------- events and functions --------------------------- */

        /// <summary>Writes a server event, optionally attributed to a player.</summary>
        public Task WriteEventAsync(string name, object? body = null, string? playerId = null, CancellationToken cancellationToken = default)
        {
            var e = new EnluEvent(name, body).ToJsonObject();
            if (!string.IsNullOrEmpty(playerId)) e["playerId"] = playerId;
            return Call("POST", Routes.Events(), e, EnluRetryMode.RejectedOnly, cancellationToken);
        }

        /// <summary>
        /// Runs a cloud function with server authority, optionally on behalf of a player. The result
        /// includes the script's logs.
        /// </summary>
        public async Task<FunctionResult<T>> ExecuteFunctionAsync<T>(string name, object? args = null, string? playerId = null,
            CancellationToken cancellationToken = default)
        {
            EnluClient.ValidateFunctionName(name);
            var body = new Dictionary<string, object?> { ["args"] = args ?? new Dictionary<string, object?>() };
            if (!string.IsNullOrEmpty(playerId)) body["playerId"] = playerId;
            return FunctionResult<T>.FromJson(await Call("POST", Routes.Function(name), body, EnluRetryMode.RejectedOnly, cancellationToken));
        }

        /// <summary>Runs a cloud function and returns its result as parsed JSON.</summary>
        public Task<FunctionResult<object>> ExecuteFunctionAsync(string name, object? args = null, string? playerId = null,
            CancellationToken cancellationToken = default) =>
            ExecuteFunctionAsync<object>(name, args, playerId, cancellationToken);

        /// <inheritdoc />
        public override string ToString() => "EnluServerApi(" + App + ")"; // never include the key
    }
}
