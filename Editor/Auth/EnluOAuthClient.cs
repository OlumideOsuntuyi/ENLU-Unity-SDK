#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu.Editor
{
    /// <summary>The signed-in developer.</summary>
    public sealed class EnluUserInfo
    {
        /// <summary>User id.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>Email.</summary>
        public string Email { get; set; } = string.Empty;
        /// <summary>Display name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Team role: <c>owner</c>, <c>admin</c>, <c>developer</c> or <c>viewer</c>.</summary>
        public string Role { get; set; } = string.Empty;
    }

    /// <summary>The developer's ENLU account (team).</summary>
    public sealed class EnluAccountInfo
    {
        /// <summary>Account id.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>Account name.</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>The answer of <c>/oauth/token</c>. Holds secrets: never log it.</summary>
    public sealed class EnluTokenSet
    {
        /// <summary>Short-lived bearer token (1 hour).</summary>
        public string AccessToken { get; set; } = string.Empty;
        /// <summary>Rotating refresh token (30 days, single use).</summary>
        public string RefreshToken { get; set; } = string.Empty;
        /// <summary>When the access token expires (UTC).</summary>
        public DateTime AccessExpiresAtUtc { get; set; }
        /// <summary>The user.</summary>
        public EnluUserInfo User { get; set; } = new EnluUserInfo();
        /// <summary>The account.</summary>
        public EnluAccountInfo Account { get; set; } = new EnluAccountInfo();

        /// <summary>Reads the token response.</summary>
        public static EnluTokenSet FromJson(object? node, DateTime nowUtc)
        {
            var o = EnluJson.AsObject(node) ?? throw new EnluException("bad_response", "Unexpected token response from the server.", 200);
            var t = new EnluTokenSet
            {
                AccessToken = EnluJson.GetString(o, "access_token") ?? string.Empty,
                RefreshToken = EnluJson.GetString(o, "refresh_token") ?? string.Empty,
                AccessExpiresAtUtc = nowUtc.AddSeconds(Math.Max(0, EnluJson.GetDouble(o, "expires_in", 3600))),
            };
            if (t.AccessToken.Length == 0 || t.RefreshToken.Length == 0 || !string.Equals(EnluJson.GetString(o, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new EnluException("bad_response", "The token response is incomplete.", 200);
            var u = EnluJson.GetObject(o, "user");
            t.User = new EnluUserInfo
            {
                Id = EnluJson.GetString(u, "id") ?? string.Empty,
                Email = EnluJson.GetString(u, "email") ?? string.Empty,
                Name = EnluJson.GetString(u, "name") ?? string.Empty,
                Role = EnluJson.GetString(u, "role") ?? string.Empty,
            };
            var a = EnluJson.GetObject(o, "account");
            t.Account = new EnluAccountInfo { Id = EnluJson.GetString(a, "id") ?? string.Empty, Name = EnluJson.GetString(a, "name") ?? string.Empty };
            return t;
        }

        /// <inheritdoc />
        public override string ToString() => "EnluTokenSet(" + User.Email + ")"; // never the tokens
    }

    /// <summary>
    /// The OAuth endpoints of an ENLU control node for the Unity Editor client
    /// (<c>/oauth/authorize</c>, <c>/oauth/token</c>, <c>/oauth/revoke</c>).
    /// </summary>
    public sealed class EnluOAuthClient
    {
        /// <summary>The registered client id of the Unity Editor.</summary>
        public const string ClientId = "enlu-unity-editor";

        private readonly HttpClient _http;
        private readonly Func<DateTime> _clock;

        /// <summary>Normalised server URL.</summary>
        public string BaseUrl { get; }

        /// <summary>Creates the client.</summary>
        public EnluOAuthClient(string baseUrl, HttpClient http, Func<DateTime>? clock = null)
        {
            BaseUrl = EnluUrl.NormalizeBaseUrl(baseUrl);
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>The browser URL that starts the sign-in.</summary>
        public string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge)
        {
            var sb = new StringBuilder(BaseUrl + "/oauth/authorize");
            EnluUrl.AppendQuery(sb, new[]
            {
                new KeyValuePair<string, string?>("client_id", ClientId),
                new KeyValuePair<string, string?>("redirect_uri", redirectUri),
                new KeyValuePair<string, string?>("state", state),
                new KeyValuePair<string, string?>("code_challenge", codeChallenge),
                new KeyValuePair<string, string?>("code_challenge_method", "S256"),
            });
            return sb.ToString();
        }

        /// <summary>Trades the one-time code and the PKCE verifier for tokens.</summary>
        public Task<EnluTokenSet> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken) =>
            TokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
            }, cancellationToken);

        /// <summary>
        /// Trades a refresh token for a new pair. The old refresh token is spent: store the new one at once.
        /// Presenting a spent token again ends the whole sign-in (<c>invalid_grant</c>).
        /// </summary>
        public Task<EnluTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
            TokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken,
            }, cancellationToken);

        /// <summary>Ends the sign-in on the server (always succeeds for unknown tokens).</summary>
        public async Task RevokeAsync(string token, CancellationToken cancellationToken)
        {
            await PostFormAsync("/oauth/revoke", new Dictionary<string, string> { ["token"] = token }, cancellationToken);
        }

        private async Task<EnluTokenSet> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
        {
            var node = await PostFormAsync("/oauth/token", form, ct);
            return EnluTokenSet.FromJson(node, _clock());
        }

        private async Task<object?> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path))
            {
                req.Content = new FormUrlEncodedContent(form);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                return await EnluEditorHttp.SendAsync(_http, req, ct);
            }
        }
    }

    /// <summary>Shared HTTP plumbing for the Editor: JSON in and out, errors as <see cref="EnluException"/>.</summary>
    internal static class EnluEditorHttp
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        public static async Task<object?> SendAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
        {
            var (status, body, retryAfter) = await SendRawAsync(http, req, ct);
            if (status >= 200 && status < 300)
            {
                if (string.IsNullOrWhiteSpace(body)) return null;
                if (!EnluJson.TryParse(body, out var node))
                    throw new EnluException("bad_response", "The server answered with something that is not JSON. Check the server URL.", status);
                return node;
            }
            throw EnluException.FromResponse(status, body, retryAfter);
        }

        public static async Task<(int Status, string Body, double? RetryAfter)> SendRawAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
        {
            using (var timeout = new CancellationTokenSource(Timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            {
                HttpResponseMessage res;
                try
                {
                    res = await http.SendAsync(req, linked.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw new EnluException("timeout", "The server did not answer in time.", 0);
                }
                catch (HttpRequestException e)
                {
                    throw new EnluException("network_error", "Could not reach the server: " + e.Message, 0, null, null, e);
                }
                using (res)
                {
                    var body = res.Content == null ? string.Empty : await res.Content.ReadAsStringAsync();
                    double? retryAfter = null;
                    if (res.Headers.RetryAfter?.Delta is TimeSpan d) retryAfter = d.TotalSeconds;
                    return ((int)res.StatusCode, body, retryAfter);
                }
            }
        }
    }
}
