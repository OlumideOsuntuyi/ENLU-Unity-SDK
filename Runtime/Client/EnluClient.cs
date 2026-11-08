#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu
{
    /// <summary>
    /// The player-facing ENLU API: accounts, player data, title data, statistics, leaderboards,
    /// events, cloud functions and match server requests.
    /// </summary>
    /// <remarks>
    /// <para>All methods are <c>async</c> and return <see cref="Task"/>; <c>await</c> them from the
    /// main thread. Requests go through <c>UnityWebRequest</c>, so the client works on every Unity
    /// platform including WebGL (no threads, no <c>System.Net.Http</c>).</para>
    /// <para>Failures throw <see cref="EnluException"/>. Transient failures are retried with backoff:
    /// 429 always (the server rejected the request before acting), 503 and network errors only on
    /// idempotent calls (reads, data writes, logins) and allocations.</para>
    /// <para>The session ticket is held in memory and, when enabled, persisted in PlayerPrefs or your
    /// <see cref="SecureStore"/>. It is never logged.</para>
    /// </remarks>
    public sealed partial class EnluClient
    {
        private const string SessionHeader = "X-Enlu-Session";
        private const int MaxEventsPerBatch = 50;

        /// <summary>
        /// Optional secure storage for the session ticket (iOS Keychain, Android Keystore, …),
        /// used when "Use secure storage" is ticked in the settings. Assign it before creating the client.
        /// </summary>
        public static IEnluKeyValueStore? SecureStore { get; set; }

        private readonly EnluHttpPipeline _http;
        private readonly IEnluKeyValueStore? _ticketStore;
        private readonly int _maxQueuedEvents;
        private readonly List<EnluEvent> _queue = new List<EnluEvent>();
        private readonly object _queueLock = new object();
        private Task<int>? _flushTask;

        private string? _ticket;
        private string? _playerId;
        private DateTime? _expiresAt;

        /// <summary>Route builder for this client's app.</summary>
        public EnluClientRoutes Routes { get; }

        /// <summary>Control node URL.</summary>
        public string BaseUrl { get; }

        /// <summary>App id or name.</summary>
        public string App { get; }

        /// <summary>True while a session ticket is held and not past its expiry.</summary>
        public bool IsLoggedIn => _ticket != null && (!_expiresAt.HasValue || _expiresAt.Value > DateTime.UtcNow);

        /// <summary>The logged-in player's id, or null.</summary>
        public string? PlayerId => IsLoggedIn ? _playerId : null;

        /// <summary>When the current session expires (UTC), or null.</summary>
        public DateTime? SessionExpiresAt => IsLoggedIn ? _expiresAt : null;

        /// <summary>Events waiting in the queue (see <see cref="QueueEvent"/>).</summary>
        public int PendingEventCount
        {
            get { lock (_queueLock) return _queue.Count; }
        }

        /// <summary>Raised after a successful login or registration.</summary>
        public event Action<LoginResult>? LoggedIn;

        /// <summary>Raised after <see cref="LogoutAsync"/> or <see cref="DeleteMyAccountAsync"/>.</summary>
        public event Action? LoggedOut;

        /// <summary>Raised when the server says the session is no longer valid (HTTP 401). Log in again.</summary>
        public event Action? SessionExpired;

        /// <summary>
        /// Creates a client with explicit plumbing (custom transports, tests, tools). In Unity use
        /// <c>new EnluClient(settings)</c> or <c>EnluClient.Default</c> instead.
        /// </summary>
        /// <param name="options">Base URL, app and timeouts.</param>
        /// <param name="transport">Sends HTTP requests.</param>
        /// <param name="scheduler">Waits between retries.</param>
        /// <param name="ticketStore">Where to persist the session ticket; null keeps it in memory only.</param>
        public EnluClient(EnluClientOptions options, IEnluTransport transport, IEnluScheduler scheduler, IEnluKeyValueStore? ticketStore = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            BaseUrl = EnluUrl.NormalizeBaseUrl(options.BaseUrl);
            App = (options.App ?? string.Empty).Trim();
            Routes = new EnluClientRoutes(BaseUrl, App);
            _http = new EnluHttpPipeline(transport, scheduler, new EnluRetryPolicy { MaxRetries = Math.Max(0, options.MaxRetries) }, options.TimeoutSeconds)
            {
                LogRequests = options.LogRequests,
            };
            _ticketStore = ticketStore;
            _maxQueuedEvents = Math.Max(MaxEventsPerBatch, options.MaxQueuedEvents);
            RestoreSession();
        }

        private string StoreKey => "enlu." + App + ".session";

        /* ------------------------------ session ------------------------------ */

        /// <summary>
        /// Uses a session ticket you stored yourself. Prefer the built-in persistence
        /// (<c>EnluSettings.persistSessionTicket</c>).
        /// </summary>
        public void SetSession(string sessionTicket, string playerId, DateTime? expiresAtUtc)
        {
            if (string.IsNullOrEmpty(sessionTicket)) throw new ArgumentException("The ticket is empty.", nameof(sessionTicket));
            _ticket = sessionTicket;
            _playerId = playerId;
            _expiresAt = expiresAtUtc;
            PersistSession();
        }

        /// <summary>Forgets the session locally without calling the server (see <see cref="LogoutAsync"/>).</summary>
        public void ClearSession()
        {
            _ticket = null;
            _playerId = null;
            _expiresAt = null;
            try
            {
                _ticketStore?.Delete(StoreKey);
            }
            catch (Exception e)
            {
                EnluLog.Warn("Could not clear the stored session: " + e.Message);
            }
        }

        private void RestoreSession()
        {
            if (_ticketStore == null) return;
            try
            {
                var raw = _ticketStore.Get(StoreKey);
                if (string.IsNullOrEmpty(raw)) return;
                if (!EnluJson.TryParse(raw!, out var node) || !(node is Dictionary<string, object?> o)) return;
                var ticket = EnluJson.GetString(o, "t");
                var player = EnluJson.GetString(o, "p");
                var expires = EnluJson.ToDateTime(EnluJson.Get(o, "e"));
                if (string.IsNullOrEmpty(ticket) || string.IsNullOrEmpty(player)) return;
                if (expires.HasValue && expires.Value <= DateTime.UtcNow.AddMinutes(1))
                {
                    _ticketStore.Delete(StoreKey);
                    return;
                }
                _ticket = ticket;
                _playerId = player;
                _expiresAt = expires;
            }
            catch (Exception e)
            {
                EnluLog.Warn("Could not restore the stored session: " + e.Message);
            }
        }

        private void PersistSession()
        {
            if (_ticketStore == null || _ticket == null) return;
            try
            {
                var o = new Dictionary<string, object?> { ["t"] = _ticket, ["p"] = _playerId, ["e"] = _expiresAt };
                _ticketStore.Set(StoreKey, EnluJson.Serialize(o));
            }
            catch (Exception e)
            {
                EnluLog.Warn("Could not store the session: " + e.Message);
            }
        }

        private LoginResult AcceptLogin(object? node)
        {
            var r = LoginResult.FromJson(node);
            _ticket = r.SessionTicket;
            _playerId = r.PlayerId;
            _expiresAt = r.ExpiresAt;
            PersistSession();
            try
            {
                LoggedIn?.Invoke(r);
            }
            catch (Exception e)
            {
                EnluLog.Error("A LoggedIn handler threw: " + e);
            }
            return r;
        }

        private async Task<object?> PlayerCall(string method, string url, object? body, EnluRetryMode mode, CancellationToken ct)
        {
            var ticket = _ticket;
            if (ticket == null || !IsLoggedIn)
                throw new EnluException("not_logged_in", "Log in first (e.g. LoginWithDeviceIdAsync).", 0);
            var headers = new Dictionary<string, string> { [SessionHeader] = ticket };
            try
            {
                return await _http.SendAsync(method, url, body, headers, mode, ct);
            }
            catch (EnluException e) when (e.HttpStatus == 401)
            {
                if (ReferenceEquals(_ticket, ticket))
                {
                    ClearSession();
                    try
                    {
                        SessionExpired?.Invoke();
                    }
                    catch (Exception handlerError)
                    {
                        EnluLog.Error("A SessionExpired handler threw: " + handlerError);
                    }
                }
                throw;
            }
        }

        /* ------------------------------- logins ------------------------------- */

        /// <summary>
        /// Logs in with your own stable id for the player (letters, digits and <c>_.:@-</c>, up to 128).
        /// With <paramref name="createAccount"/> the account is created on first use.
        /// </summary>
        public async Task<LoginResult> LoginWithCustomIdAsync(string customId, bool createAccount = true, CancellationToken cancellationToken = default)
        {
            if (!EnluIds.IsValidCustomId(customId))
                throw new ArgumentException("Custom ids use letters, digits and _.:@- (1 to 128 characters).", nameof(customId));
            var body = new Dictionary<string, object?> { ["customId"] = customId, ["createAccount"] = createAccount };
            return AcceptLogin(await _http.SendAsync("POST", Routes.LoginCustom(), body, null, EnluRetryMode.Idempotent, cancellationToken));
        }

        /// <summary>
        /// Logs in with a device identifier you supply (it becomes the custom id <c>device:&lt;id&gt;</c>).
        /// In Unity, <c>LoginWithDeviceIdAsync()</c> without arguments picks the device id for you.
        /// </summary>
        public Task<LoginResult> LoginWithDeviceIdAsync(string rawDeviceId, bool createAccount = true, CancellationToken cancellationToken = default)
        {
            var customId = EnluIds.DeviceCustomId(rawDeviceId)
                ?? throw new ArgumentException("The device id has no usable characters.", nameof(rawDeviceId));
            return LoginWithCustomIdAsync(customId, createAccount, cancellationToken);
        }

        /// <summary>Creates an account with email and password (8 to 256 characters) and logs in.</summary>
        public async Task<LoginResult> RegisterWithEmailAsync(string email, string password, string? displayName = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Email is required.", nameof(email));
            if (password == null || password.Length < 8 || password.Length > 256) throw new ArgumentException("Passwords are 8 to 256 characters.", nameof(password));
            var body = new Dictionary<string, object?> { ["email"] = email, ["password"] = password };
            if (!string.IsNullOrEmpty(displayName)) body["displayName"] = displayName;
            return AcceptLogin(await _http.SendAsync("POST", Routes.Register(), body, null, EnluRetryMode.RejectedOnly, cancellationToken));
        }

        /// <summary>Logs in with email and password.</summary>
        public async Task<LoginResult> LoginWithEmailAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Email is required.", nameof(email));
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password is required.", nameof(password));
            var body = new Dictionary<string, object?> { ["email"] = email, ["password"] = password };
            return AcceptLogin(await _http.SendAsync("POST", Routes.LoginEmail(), body, null, EnluRetryMode.Idempotent, cancellationToken));
        }

        /// <summary>
        /// Ends the session on the server and forgets it locally. The local session is cleared even when
        /// the server cannot be reached. Queued events are flushed first (best effort).
        /// </summary>
        public async Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            var ticket = _ticket;
            if (ticket == null) return;
            if (PendingEventCount > 0)
            {
                try
                {
                    await FlushAsync(cancellationToken);
                }
                catch (EnluException e)
                {
                    EnluLog.Warn("Events could not be sent before logout: " + e.Message);
                }
            }
            try
            {
                await _http.SendAsync("POST", Routes.Logout(), new Dictionary<string, object?>(),
                    new Dictionary<string, string> { [SessionHeader] = ticket }, EnluRetryMode.RejectedOnly, cancellationToken);
            }
            catch (EnluException e)
            {
                EnluLog.Warn("Logout could not reach the server; the local session was cleared anyway (" + e.Code + ").");
            }
            finally
            {
                ClearSession();
                lock (_queueLock) _queue.Clear();
                try
                {
                    LoggedOut?.Invoke();
                }
                catch (Exception e)
                {
                    EnluLog.Error("A LoggedOut handler threw: " + e);
                }
            }
        }

        /* ------------------------------- profile ------------------------------ */

        /// <summary>Returns the logged-in player's profile and statistics.</summary>
        public async Task<PlayerProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
            PlayerProfile.FromJson(await PlayerCall("GET", Routes.Me(), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>Sets the display name shown on leaderboards (1 to 64 printable characters; null clears it).</summary>
        public async Task<PlayerProfile> UpdateDisplayNameAsync(string? displayName, CancellationToken cancellationToken = default)
        {
            if (displayName != null && (displayName.Length == 0 || displayName.Length > 64))
                throw new ArgumentException("Display names are 1 to 64 characters.", nameof(displayName));
            var body = new Dictionary<string, object?> { ["displayName"] = displayName };
            return PlayerProfile.FromJson(await PlayerCall("PATCH", Routes.Me(), body, EnluRetryMode.Idempotent, cancellationToken));
        }

        /// <summary>Downloads everything stored about the player (profile, data, statistics, events).</summary>
        public async Task<PlayerExport> ExportMyDataAsync(CancellationToken cancellationToken = default) =>
            PlayerExport.FromJson(await PlayerCall("GET", Routes.MeExport(), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>
        /// Permanently erases the player's account and everything about them, then logs out locally.
        /// This cannot be undone: ask the player to confirm first. Returns the deleted player id.
        /// </summary>
        public async Task<string> DeleteMyAccountAsync(CancellationToken cancellationToken = default)
        {
            var node = await PlayerCall("DELETE", Routes.Me(), null, EnluRetryMode.RejectedOnly, cancellationToken);
            var deleted = EnluJson.GetString(EnluJson.AsObject(node), "deleted") ?? _playerId ?? string.Empty;
            ClearSession();
            lock (_queueLock) _queue.Clear();
            try
            {
                LoggedOut?.Invoke();
            }
            catch (Exception e)
            {
                EnluLog.Error("A LoggedOut handler threw: " + e);
            }
            return deleted;
        }

        /* ----------------------------- player data ---------------------------- */

        /// <summary>Reads the player's own data. Pass keys to read only those; none reads all keys.</summary>
        public async Task<Dictionary<string, PlayerDataEntry>> GetDataAsync(IEnumerable<string>? keys = null, CancellationToken cancellationToken = default) =>
            PlayerDataEntry.MapFromResponse(await PlayerCall("GET", Routes.Data(keys), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>Reads the given keys of the player's own data.</summary>
        public Task<Dictionary<string, PlayerDataEntry>> GetDataAsync(params string[] keys) => GetDataAsync((IEnumerable<string>)keys);

        /// <summary>
        /// Writes player data keys (up to 100 per call, 64 KB per value). Values may be dictionaries,
        /// lists, primitives or <c>[Serializable]</c> objects; a null value deletes the key.
        /// Returns the number of keys written.
        /// </summary>
        public async Task<int> SetDataAsync(IDictionary<string, object?> data, DataVisibility visibility = DataVisibility.Private, CancellationToken cancellationToken = default)
        {
            ValidateData(data);
            var body = new Dictionary<string, object?> { ["data"] = data, ["visibility"] = EnluEnumText.ToApi(visibility) };
            var node = await PlayerCall("PUT", Routes.Data(), body, EnluRetryMode.Idempotent, cancellationToken);
            return EnluJson.GetInt(EnluJson.AsObject(node), "updated");
        }

        /// <summary>Writes one player data key. A null value deletes it.</summary>
        public Task<int> SetDataAsync(string key, object? value, DataVisibility visibility = DataVisibility.Private, CancellationToken cancellationToken = default) =>
            SetDataAsync(new Dictionary<string, object?> { [key] = value }, visibility, cancellationToken);

        internal static void ValidateData(IDictionary<string, object?> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Count == 0) throw new ArgumentException("Pass at least one key.", nameof(data));
            if (data.Count > 100) throw new ArgumentException("At most 100 keys per call.", nameof(data));
            foreach (var k in data.Keys)
                if (!EnluIds.IsValidDataKey(k)) throw new ArgumentException("Data keys use letters, digits and _.:- (1 to 128): " + k, nameof(data));
        }

        /// <summary>Reads another player's public data keys.</summary>
        public async Task<Dictionary<string, PlayerDataEntry>> GetPlayerPublicDataAsync(string playerId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("playerId is required.", nameof(playerId));
            return PlayerDataEntry.MapFromResponse(await PlayerCall("GET", Routes.PlayerData(playerId), null, EnluRetryMode.Idempotent, cancellationToken));
        }

        /// <summary>Reads title data (game-wide configuration). Pass keys to read only those.</summary>
        public async Task<Dictionary<string, object?>> GetTitleDataAsync(IEnumerable<string>? keys = null, CancellationToken cancellationToken = default)
        {
            var node = await PlayerCall("GET", Routes.TitleData(keys), null, EnluRetryMode.Idempotent, cancellationToken);
            return EnluJson.GetObject(EnluJson.AsObject(node), "data") ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        /* ----------------------- statistics and leaderboards ---------------------- */

        /// <summary>
        /// Submits statistic values (only statistics defined as client-writable). Returns the stored
        /// values after aggregation (max, min, sum or last).
        /// </summary>
        public async Task<Dictionary<string, double>> UpdateStatisticsAsync(IDictionary<string, double> statistics, CancellationToken cancellationToken = default)
        {
            if (statistics == null || statistics.Count == 0) throw new ArgumentException("Pass at least one statistic.", nameof(statistics));
            foreach (var kv in statistics)
            {
                if (!EnluIds.IsValidName(kv.Key)) throw new ArgumentException("Invalid statistic name: " + kv.Key, nameof(statistics));
                if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value)) throw new ArgumentException("Statistic values must be finite: " + kv.Key, nameof(statistics));
            }
            var node = await PlayerCall("POST", Routes.Statistics(), new Dictionary<string, object?> { ["statistics"] = statistics }, EnluRetryMode.RejectedOnly, cancellationToken);
            return ReadNumberMap(EnluJson.GetObject(EnluJson.AsObject(node), "statistics"));
        }

        /// <summary>Submits one statistic value.</summary>
        public Task<Dictionary<string, double>> UpdateStatisticAsync(string name, double value, CancellationToken cancellationToken = default) =>
            UpdateStatisticsAsync(new Dictionary<string, double> { [name] = value }, cancellationToken);

        internal static Dictionary<string, double> ReadNumberMap(Dictionary<string, object?>? o)
        {
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            if (o == null) return map;
            foreach (var kv in o) if (EnluJson.IsNumber(kv.Value)) map[kv.Key] = EnluJson.ToDouble(kv.Value);
            return map;
        }

        /// <summary>
        /// Reads a leaderboard: the top <paramref name="top"/> (1 to 100) or, with
        /// <paramref name="aroundMe"/>, a page centred on the logged-in player.
        /// </summary>
        public async Task<Leaderboard> GetLeaderboardAsync(string name, int top = 10, bool aroundMe = false, CancellationToken cancellationToken = default)
        {
            if (!EnluIds.IsValidName(name)) throw new ArgumentException("Invalid statistic name: " + name, nameof(name));
            top = Math.Max(1, Math.Min(100, top));
            return Leaderboard.FromJson(await PlayerCall("GET", Routes.Leaderboard(name, top, aroundMe), null, EnluRetryMode.Idempotent, cancellationToken));
        }

        /* -------------------------------- events -------------------------------- */

        /// <summary>Writes one event now (analytics, triggers). Body up to 16 KB as JSON.</summary>
        public async Task WriteEventAsync(string name, object? body = null, CancellationToken cancellationToken = default)
        {
            var e = new EnluEvent(name, body);
            await PlayerCall("POST", Routes.Events(), e.ToJsonObject(), EnluRetryMode.RejectedOnly, cancellationToken);
        }

        /// <summary>Writes up to 50 events in one request. Returns the number written.</summary>
        public async Task<int> WriteEventsAsync(IList<EnluEvent> events, CancellationToken cancellationToken = default)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            if (events.Count == 0) return 0;
            if (events.Count > MaxEventsPerBatch) throw new ArgumentException("At most 50 events per request; use QueueEvent and FlushAsync for more.", nameof(events));
            var list = new List<object?>(events.Count);
            foreach (var e in events) list.Add(e.ToJsonObject());
            var node = await PlayerCall("POST", Routes.Events(), new Dictionary<string, object?> { ["events"] = list }, EnluRetryMode.RejectedOnly, cancellationToken);
            return EnluJson.GetInt(EnluJson.AsObject(node), "written");
        }

        /// <summary>
        /// Adds an event to the batch queue. The queue is sent in batches of 50 by <see cref="FlushAsync"/>,
        /// automatically when 50 are waiting, and (in Unity) every few seconds and when the app pauses.
        /// </summary>
        public void QueueEvent(string name, object? body = null)
        {
            var e = new EnluEvent(name, body);
            int count;
            lock (_queueLock)
            {
                _queue.Add(e);
                if (_queue.Count > _maxQueuedEvents) _queue.RemoveRange(0, _queue.Count - _maxQueuedEvents);
                count = _queue.Count;
            }
            if (count >= MaxEventsPerBatch && IsLoggedIn) FlushInBackground();
        }

        internal async void FlushInBackground()
        {
            try
            {
                await FlushAsync();
            }
            catch (Exception e)
            {
                EnluLog.Warn("Queued events not sent yet: " + e.Message);
            }
        }

        /// <summary>
        /// Sends all queued events in batches of 50. Failed batches go back to the front of the queue.
        /// Returns the number of events written. Concurrent calls share one flush.
        /// </summary>
        public Task<int> FlushAsync(CancellationToken cancellationToken = default)
        {
            lock (_queueLock)
            {
                if (_flushTask != null && !_flushTask.IsCompleted) return _flushTask;
                _flushTask = FlushCore(cancellationToken);
                return _flushTask;
            }
        }

        private async Task<int> FlushCore(CancellationToken cancellationToken)
        {
            var written = 0;
            while (true)
            {
                List<EnluEvent> batch;
                lock (_queueLock)
                {
                    if (_queue.Count == 0) return written;
                    var n = Math.Min(MaxEventsPerBatch, _queue.Count);
                    batch = _queue.GetRange(0, n);
                    _queue.RemoveRange(0, n);
                }
                try
                {
                    written += await WriteEventsAsync(batch, cancellationToken);
                }
                catch
                {
                    lock (_queueLock)
                    {
                        _queue.InsertRange(0, batch);
                        if (_queue.Count > _maxQueuedEvents) _queue.RemoveRange(_maxQueuedEvents, _queue.Count - _maxQueuedEvents);
                    }
                    throw;
                }
            }
        }

        /* ------------------------------- functions ------------------------------ */

        /// <summary>
        /// Runs a cloud function as the logged-in player. <paramref name="args"/> may be a dictionary,
        /// list, primitive or <c>[Serializable]</c> object. The result is converted to <typeparamref name="T"/>
        /// (see <see cref="EnluJson.ConvertTo{T}(object)"/>). A function that throws returns
        /// <c>Ok == false</c> rather than throwing; call <c>EnsureOk()</c> if you prefer exceptions.
        /// </summary>
        public async Task<FunctionResult<T>> ExecuteFunctionAsync<T>(string name, object? args = null, CancellationToken cancellationToken = default)
        {
            ValidateFunctionName(name);
            var body = new Dictionary<string, object?> { ["args"] = args ?? new Dictionary<string, object?>() };
            return FunctionResult<T>.FromJson(await PlayerCall("POST", Routes.Function(name), body, EnluRetryMode.RejectedOnly, cancellationToken));
        }

        /// <summary>Runs a cloud function and returns its result as parsed JSON.</summary>
        public Task<FunctionResult<object>> ExecuteFunctionAsync(string name, object? args = null, CancellationToken cancellationToken = default) =>
            ExecuteFunctionAsync<object>(name, args, cancellationToken);

        internal static void ValidateFunctionName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64) throw new ArgumentException("Function names are 1 to 64 characters.", nameof(name));
            var first = name[0];
            if (!(char.IsLetter(first) && first < 128) && first != '_' && first != '$')
                throw new ArgumentException("Function names start with a letter, _ or $: " + name, nameof(name));
            foreach (var c in name)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '$'))
                    throw new ArgumentException("Function names use letters, digits, _ and $: " + name, nameof(name));
        }

        /* ----------------------------- match servers ----------------------------- */

        /// <summary>
        /// Asks for a game server for the logged-in player (only when the app allows client allocations;
        /// otherwise your backend allocates with <see cref="EnluServerApi"/>). Retries while no standby
        /// server is free (HTTP 503) up to the retry limit, then throws with code <c>unavailable</c>.
        /// </summary>
        /// <param name="workload">The game-server workload name.</param>
        /// <param name="regions">Preferred regions, best first; null or empty for any.</param>
        /// <param name="metadata">String metadata for the server (map, mode…).</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public async Task<SessionAllocation> RequestSessionAsync(string workload, IEnumerable<string>? regions = null,
            IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workload)) throw new ArgumentException("workload is required.", nameof(workload));
            var body = new Dictionary<string, object?> { ["workload"] = workload };
            if (regions != null)
            {
                var r = new List<string>(regions);
                if (r.Count > 0) body["regions"] = r;
            }
            if (metadata != null && metadata.Count > 0) body["metadata"] = metadata;
            return SessionAllocation.FromJson(await PlayerCall("POST", Routes.Sessions(), body, EnluRetryMode.RejectedOrUnavailable, cancellationToken));
        }

        /// <inheritdoc />
        public override string ToString() =>
            "EnluClient(" + App + " @ " + BaseUrl + ", " + (IsLoggedIn ? "player " + _playerId : "not logged in") + ", " +
            PendingEventCount.ToString(CultureInfo.InvariantCulture) + " queued events)";
    }
}
