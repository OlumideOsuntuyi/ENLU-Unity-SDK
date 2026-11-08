#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu.Editor
{
    /// <summary>Sign-in state of the Editor.</summary>
    public enum EnluAuthStatus
    {
        /// <summary>Not signed in.</summary>
        SignedOut = 0,
        /// <summary>Waiting for the developer to approve in the browser.</summary>
        SigningIn = 1,
        /// <summary>Restoring a stored sign-in.</summary>
        Restoring = 2,
        /// <summary>Signed in.</summary>
        SignedIn = 3,
    }

    /// <summary>
    /// Keeps the short-lived part of a sign-in (access token, expiry, user and account) across Unity
    /// domain reloads. The Editor implementation uses <c>SessionState</c> (memory of the Editor process,
    /// gone when Unity quits). Never use a file or EditorPrefs.
    /// </summary>
    public interface IEnluTokenCache
    {
        /// <summary>Reads the cached JSON for a host, or null.</summary>
        string? Load(string hostKey);
        /// <summary>Writes (or with null, clears) the cached JSON for a host.</summary>
        void Save(string hostKey, string? json);
    }

    /// <summary>An in-memory <see cref="IEnluTokenCache"/> (tests).</summary>
    public sealed class EnluMemoryTokenCache : IEnluTokenCache
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <inheritdoc />
        public string? Load(string hostKey) => _values.TryGetValue(hostKey, out var v) ? v : null;
        /// <inheritdoc />
        public void Save(string hostKey, string? json)
        {
            if (json == null) _values.Remove(hostKey);
            else _values[hostKey] = json;
        }
    }

    /// <summary>
    /// The developer's sign-in from the Unity Editor to one ENLU server: OAuth 2.0 for native apps
    /// (RFC 8252) with PKCE (RFC 7636) through the system browser and a loopback redirect.
    /// </summary>
    /// <remarks>
    /// <para>The Editor never sees the password. The access token (1 hour) lives in memory and in the
    /// <see cref="IEnluTokenCache"/>; the refresh token (30 days, rotating) lives only in the
    /// <see cref="IEnluCredentialStore"/> (the OS credential store). Neither is ever logged.</para>
    /// <para>Refreshes are single-flight: concurrent requests that find the token expired share one
    /// refresh, because presenting a spent refresh token twice ends the sign-in on the server.</para>
    /// </remarks>
    public sealed class EnluEditorAuth
    {
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
        private readonly object _lock = new object();
        private readonly HttpClient _http;
        private readonly IEnluCredentialStore _store;
        private readonly IEnluTokenCache _cache;
        private readonly Func<DateTime> _clock;
        private readonly EnluOAuthClient _oauth;

        private string? _access;
        private string? _refresh;
        private DateTime _expiresAt;
        private Task? _refreshTask;
        private CancellationTokenSource? _signInCts;

        /// <summary>Server URL.</summary>
        public string BaseUrl { get; }
        /// <summary>Key used in the credential store (host and port).</summary>
        public string HostKey { get; }
        /// <summary>Current status.</summary>
        public EnluAuthStatus Status { get; private set; }
        /// <summary>The signed-in user, or null.</summary>
        public EnluUserInfo? User { get; private set; }
        /// <summary>The user's account, or null.</summary>
        public EnluAccountInfo? Account { get; private set; }
        /// <summary>The last problem, for the status line (never contains secrets).</summary>
        public string? LastMessage { get; private set; }
        /// <summary>Where the refresh token is kept.</summary>
        public string StoreDescription => _store.Description;
        /// <summary>False when the sign-in will not survive an Editor restart.</summary>
        public bool IsPersistent => _store.IsPersistent;
        /// <summary>How long a browser sign-in may take.</summary>
        public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Raised whenever the status, user or message changes.</summary>
        public event Action? Changed;

        /// <summary>Creates the sign-in manager for one server.</summary>
        public EnluEditorAuth(string baseUrl, IEnluCredentialStore store, IEnluTokenCache cache, HttpClient http, Func<DateTime>? clock = null)
        {
            BaseUrl = EnluUrl.NormalizeBaseUrl(baseUrl);
            HostKey = EnluCredentialStores.HostKey(BaseUrl);
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _clock = clock ?? (() => DateTime.UtcNow);
            _oauth = new EnluOAuthClient(BaseUrl, http, _clock);
            LoadCache();
        }

        /// <summary>True when signed in (the access token may still need a refresh).</summary>
        public bool IsSignedIn => Status == EnluAuthStatus.SignedIn;

        private void Notify(string? message = null)
        {
            if (message != null) LastMessage = message;
            try
            {
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // UI handlers must not break the sign-in.
            }
        }

        /* ------------------------------- state ------------------------------- */

        private void LoadCache()
        {
            var json = _cache.Load(HostKey);
            if (json == null || !EnluJson.TryParse(json, out var node) || !(node is Dictionary<string, object?> o)) return;
            var access = EnluJson.GetString(o, "a");
            var expires = EnluJson.ToDateTime(EnluJson.Get(o, "e"));
            if (string.IsNullOrEmpty(access) || !expires.HasValue) return;
            _access = access;
            _expiresAt = expires.Value;
            User = new EnluUserInfo
            {
                Id = EnluJson.GetString(o, "uid") ?? string.Empty,
                Email = EnluJson.GetString(o, "email") ?? string.Empty,
                Name = EnluJson.GetString(o, "name") ?? string.Empty,
                Role = EnluJson.GetString(o, "role") ?? string.Empty,
            };
            Account = new EnluAccountInfo { Id = EnluJson.GetString(o, "aid") ?? string.Empty, Name = EnluJson.GetString(o, "aname") ?? string.Empty };
            Status = EnluAuthStatus.SignedIn;
        }

        private void Accept(EnluTokenSet t)
        {
            // Rotation: the new refresh token replaces the spent one before anything else can fail.
            string? storeProblem = null;
            try
            {
                _store.Write(HostKey, t.RefreshToken);
            }
            catch (Exception e)
            {
                // Keep it in memory: this Editor session works, the next one needs a new sign-in.
                storeProblem = "Signed in, but the sign-in could not be saved to " + _store.Description + " (" + e.Message + "). You will need to sign in again after restarting Unity.";
            }
            lock (_lock)
            {
                _access = t.AccessToken;
                _refresh = t.RefreshToken;
                _expiresAt = t.AccessExpiresAtUtc;
            }
            User = t.User;
            Account = t.Account;
            _cache.Save(HostKey, EnluJson.Serialize(new Dictionary<string, object?>
            {
                ["a"] = t.AccessToken,
                ["e"] = t.AccessExpiresAtUtc,
                ["uid"] = t.User.Id,
                ["email"] = t.User.Email,
                ["name"] = t.User.Name,
                ["role"] = t.User.Role,
                ["aid"] = t.Account.Id,
                ["aname"] = t.Account.Name,
            }));
            Status = EnluAuthStatus.SignedIn;
            Notify(storeProblem ?? string.Empty);
        }

        private void ClearLocal(string message)
        {
            lock (_lock)
            {
                _access = null;
                _refresh = null;
                _expiresAt = DateTime.MinValue;
            }
            try
            {
                _store.Delete(HostKey);
            }
            catch (Exception)
            {
                // A store that cannot delete must not keep the developer signed in locally.
            }
            _cache.Save(HostKey, null);
            User = null;
            Account = null;
            Status = EnluAuthStatus.SignedOut;
            Notify(message);
        }

        /* ------------------------------ sign-in ------------------------------ */

        /// <summary>
        /// Signs in through the browser. <paramref name="openBrowser"/> receives the authorize URL
        /// (in Unity: <c>Application.OpenURL</c>). Completes when the developer approved, or throws
        /// <see cref="EnluException"/> (<c>access_denied</c>, <c>state_mismatch</c>, <c>timeout</c>, …)
        /// or <see cref="OperationCanceledException"/> after <see cref="CancelSignIn"/>.
        /// </summary>
        public async Task SignInAsync(Action<string> openBrowser, CancellationToken cancellationToken = default)
        {
            if (openBrowser == null) throw new ArgumentNullException(nameof(openBrowser));
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (Status == EnluAuthStatus.SigningIn) throw new InvalidOperationException("A sign-in is already waiting for the browser.");
                _signInCts?.Dispose();
                cts = _signInCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }
            var previous = Status;
            Status = EnluAuthStatus.SigningIn;
            Notify("Waiting for you to approve in the browser…");
            try
            {
                var verifier = EnluPkce.CreateVerifier();
                var state = EnluPkce.CreateState();
                EnluTokenSet tokens;
                using (var listener = EnluLoopbackListener.Start())
                {
                    openBrowser(_oauth.BuildAuthorizeUrl(listener.RedirectUri, state, EnluPkce.ChallengeFor(verifier)));
                    EnluCallbackResult cb;
                    try
                    {
                        cb = await listener.WaitForCallbackAsync(state, SignInTimeout, cts.Token);
                    }
                    catch (TimeoutException)
                    {
                        throw new EnluException("timeout", "The sign-in timed out. Start again when you are ready.", 0);
                    }
                    switch (cb.Kind)
                    {
                        case EnluCallbackKind.Approved:
                            break;
                        case EnluCallbackKind.Denied:
                            throw new EnluException(cb.Error == "access_denied" ? "access_denied" : "authorize_error",
                                cb.Error == "access_denied" ? "Sign-in was cancelled in the browser." : "The server refused the sign-in (" + cb.Error + ").", 0);
                        case EnluCallbackKind.StateMismatch:
                            throw new EnluException("state_mismatch", "The browser answered for a different sign-in. Start again from Unity.", 0);
                        default:
                            throw new EnluException("bad_response", "The browser returned an incomplete answer. Start again from Unity.", 0);
                    }
                    tokens = await _oauth.ExchangeCodeAsync(cb.Code!, listener.RedirectUri, verifier, cts.Token);
                }
                Accept(tokens);
            }
            catch (Exception e)
            {
                if (Status == EnluAuthStatus.SigningIn) Status = previous == EnluAuthStatus.SignedIn ? EnluAuthStatus.SignedIn : EnluAuthStatus.SignedOut;
                Notify(e is OperationCanceledException ? "Sign-in cancelled." : e.Message);
                throw;
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_signInCts, cts)) _signInCts = null;
                }
                cts.Dispose();
            }
        }

        /// <summary>Stops waiting for the browser.</summary>
        public void CancelSignIn()
        {
            CancellationTokenSource? cts;
            lock (_lock) cts = _signInCts;
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Restores a sign-in from a previous Editor session: uses the cached access token when it is
        /// still valid, otherwise refreshes with the stored refresh token. Returns true when signed in.
        /// A network failure keeps the stored token for later; a rejected token signs out.
        /// </summary>
        public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
        {
            if (Status == EnluAuthStatus.SigningIn) return false;
            if (Status == EnluAuthStatus.SignedIn && _clock() < _expiresAt - RefreshMargin) return true;
            string? stored;
            try
            {
                stored = _store.Read(HostKey);
            }
            catch (Exception e)
            {
                Notify("Could not read the credential store: " + e.Message);
                return false;
            }
            if (string.IsNullOrEmpty(stored))
            {
                if (Status == EnluAuthStatus.SignedIn) ClearLocal("Your sign-in expired. Sign in again.");
                return false;
            }
            Status = EnluAuthStatus.Restoring;
            Notify("Restoring your sign-in…");
            try
            {
                await RefreshAsync(null, cancellationToken);
                return true;
            }
            catch (EnluException e) when (e.IsNetworkError)
            {
                Status = EnluAuthStatus.SignedOut;
                Notify("Offline: could not restore the sign-in (" + e.Message + ").");
                return false;
            }
            catch (EnluException)
            {
                return false; // RefreshAsync already signed out with a message.
            }
        }

        /// <summary>Signs out: revokes the sign-in on the server (best effort) and deletes the stored token.</summary>
        public async Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            CancelSignIn();
            string? token;
            lock (_lock) token = _refresh;
            if (token == null)
            {
                try
                {
                    token = _store.Read(HostKey);
                }
                catch (Exception)
                {
                    token = null;
                }
            }
            token ??= _access;
            try
            {
                if (token != null) await _oauth.RevokeAsync(token, cancellationToken);
            }
            catch (EnluException)
            {
                // The local sign-out still happens; the server-side grant expires on its own.
            }
            finally
            {
                ClearLocal("Signed out.");
            }
        }

        /* ------------------------------ refresh ------------------------------ */

        /// <summary>
        /// Refreshes unless another request already did (<paramref name="usedAccess"/> is the token that
        /// failed; null forces a refresh). Concurrent callers share one refresh.
        /// </summary>
        private Task RefreshAsync(string? usedAccess, CancellationToken ct)
        {
            lock (_lock)
            {
                if (usedAccess != null && _access != null && !string.Equals(usedAccess, _access, StringComparison.Ordinal) && _clock() < _expiresAt - RefreshMargin)
                    return Task.CompletedTask; // someone refreshed meanwhile
                // Never cancelled: stopping between the server's rotation and storing the new token would
                // lose the only valid refresh token. Callers stop waiting through WaitAsync instead.
                if (_refreshTask == null || _refreshTask.IsCompleted) _refreshTask = DoRefreshAsync();
                return WaitAsync(_refreshTask, ct);
            }
        }

        private static async Task WaitAsync(Task task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled || task.IsCompleted)
            {
                await task;
                return;
            }
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task) != task) throw new OperationCanceledException(ct);
            }
            await task;
        }

        private async Task DoRefreshAsync()
        {
            string? rt;
            lock (_lock) rt = _refresh;
            rt ??= _store.Read(HostKey);
            if (string.IsNullOrEmpty(rt))
            {
                ClearLocal("Your sign-in ended. Sign in again.");
                throw new EnluException("signed_out", "Sign in again.", 401);
            }
            EnluTokenSet t;
            try
            {
                t = await _oauth.RefreshAsync(rt!, CancellationToken.None);
            }
            catch (EnluException e) when (e.Code == "invalid_grant" || e.Code == "invalid_client")
            {
                ClearLocal(e.Message);
                throw new EnluException("signed_out", e.Message, e.HttpStatus);
            }
            Accept(t);
        }

        /* --------------------------------- API --------------------------------- */

        /// <summary>
        /// Calls the ENLU API as the signed-in developer (<c>Authorization: Bearer</c>). Refreshes the
        /// access token shortly before it expires, or once after a 401. Throws <see cref="EnluException"/>
        /// (403 <c>forbidden</c> when the developer's team role does not allow the action).
        /// </summary>
        public async Task<object?> SendAsync(string method, string path, object? body = null, CancellationToken cancellationToken = default)
        {
            if (Status != EnluAuthStatus.SignedIn) throw new EnluException("not_signed_in", "Sign in to ENLU first (Tools > ENLU > Account).", 0);
            if (string.IsNullOrEmpty(path) || path[0] != '/') throw new ArgumentException("Paths start with '/'.", nameof(path));
            bool expiring;
            lock (_lock) expiring = _access == null || _clock() >= _expiresAt - RefreshMargin;
            if (expiring) await RefreshAsync(null, cancellationToken);

            string used;
            lock (_lock) used = _access ?? throw new EnluException("not_signed_in", "Sign in again.", 0);
            var (status, text, retryAfter) = await SendOnceAsync(method, path, body, used, cancellationToken);
            if (status == 401)
            {
                await RefreshAsync(used, cancellationToken);
                lock (_lock) used = _access ?? throw new EnluException("not_signed_in", "Sign in again.", 0);
                (status, text, retryAfter) = await SendOnceAsync(method, path, body, used, cancellationToken);
            }
            if (status >= 200 && status < 300)
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                if (!EnluJson.TryParse(text, out var node)) throw new EnluException("bad_response", "The server answered with something that is not JSON.", status);
                return node;
            }
            var error = EnluException.FromResponse(status, text, retryAfter);
            if (status == 401) ClearLocal("Your sign-in ended. Sign in again.");
            throw error;
        }

        private async Task<(int, string, double?)> SendOnceAsync(string method, string path, object? body, string accessToken, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(new HttpMethod(method), BaseUrl + path))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (body != null) req.Content = new StringContent(EnluJson.Serialize(body), Encoding.UTF8, "application/json");
                return await EnluEditorHttp.SendRawAsync(_http, req, ct);
            }
        }

        /// <summary>Test hook: pretend the access token expires at <paramref name="utc"/>.</summary>
        internal void SetAccessExpiryForTests(DateTime utc)
        {
            lock (_lock) _expiresAt = utc;
        }

        /// <summary>Test hook: the refresh token currently held (never expose this in UI).</summary>
        internal string? RefreshTokenForTests
        {
            get { lock (_lock) return _refresh ?? _store.Read(HostKey); }
        }
    }
}
