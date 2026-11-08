#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Enlu
{
    /// <summary>
    /// Builds the URLs of the player-facing API (<c>/client/v1/:app/…</c>) for one app.
    /// Exposed for tests and for custom transports; most code should use <see cref="EnluClient"/>.
    /// </summary>
    public sealed class EnluClientRoutes
    {
        private readonly string _base;
        private readonly string _app;

        /// <summary>Creates the route builder for one app (id or name) on one control node.</summary>
        public EnluClientRoutes(string baseUrl, string app)
        {
            _base = EnluUrl.NormalizeBaseUrl(baseUrl);
            if (string.IsNullOrWhiteSpace(app)) throw new ArgumentException("The app id or name is empty. Set it in your EnluSettings asset.", nameof(app));
            _app = app.Trim();
        }

        private string U(IEnumerable<KeyValuePair<string, string?>>? query, params string[] segments)
        {
            var all = new List<string>(segments.Length + 1) { _app };
            all.AddRange(segments);
            return EnluUrl.Build(_base, "client/v1", all, query);
        }

        private static KeyValuePair<string, string?> Q(string k, string? v) => new KeyValuePair<string, string?>(k, v);

        /// <summary><c>POST /login/custom</c></summary>
        public string LoginCustom() => U(null, "login", "custom");
        /// <summary><c>POST /login/email</c></summary>
        public string LoginEmail() => U(null, "login", "email");
        /// <summary><c>POST /register</c></summary>
        public string Register() => U(null, "register");
        /// <summary><c>POST /logout</c></summary>
        public string Logout() => U(null, "logout");
        /// <summary><c>GET|PATCH|DELETE /me</c></summary>
        public string Me() => U(null, "me");
        /// <summary><c>GET /me/export</c></summary>
        public string MeExport() => U(null, "me", "export");

        /// <summary><c>GET|PUT /data</c> with optional <c>?keys=a,b</c>.</summary>
        public string Data(IEnumerable<string>? keys = null) => U(KeysQuery(keys), "data");

        /// <summary><c>GET /players/:player/data</c> (another player's public data).</summary>
        public string PlayerData(string playerId) => U(null, "players", playerId, "data");

        /// <summary><c>GET /title-data</c> with optional <c>?keys=a,b</c>.</summary>
        public string TitleData(IEnumerable<string>? keys = null) => U(KeysQuery(keys), "title-data");

        /// <summary><c>POST /statistics</c></summary>
        public string Statistics() => U(null, "statistics");

        /// <summary><c>GET /leaderboards/:name?top=N[&amp;around=me]</c></summary>
        public string Leaderboard(string name, int top, bool aroundMe) =>
            U(new[] { Q("top", top.ToString(CultureInfo.InvariantCulture)), Q("around", aroundMe ? "me" : null) }, "leaderboards", name);

        /// <summary><c>POST /events</c></summary>
        public string Events() => U(null, "events");

        /// <summary><c>POST /functions/:fn</c></summary>
        public string Function(string name) => U(null, "functions", name);

        /// <summary><c>POST /sessions</c></summary>
        public string Sessions() => U(null, "sessions");

        internal static IEnumerable<KeyValuePair<string, string?>>? KeysQuery(IEnumerable<string>? keys)
        {
            if (keys == null) return null;
            var list = new List<string>();
            foreach (var k in keys)
            {
                if (string.IsNullOrEmpty(k)) continue;
                if (k.IndexOf(',') >= 0) throw new ArgumentException("Data keys cannot contain ',': " + k, nameof(keys));
                list.Add(k);
            }
            if (list.Count == 0) return null;
            return new[] { Q("keys", string.Join(",", list)) };
        }
    }

    /// <summary>
    /// Builds the URLs of the server-side API (<c>/v1/apps/:app/…</c>) for one app.
    /// </summary>
    public sealed class EnluServerRoutes
    {
        private readonly string _base;
        private readonly string _app;

        /// <summary>Creates the route builder for one app (id or name) on one control node.</summary>
        public EnluServerRoutes(string baseUrl, string app)
        {
            _base = EnluUrl.NormalizeBaseUrl(baseUrl);
            if (string.IsNullOrWhiteSpace(app)) throw new ArgumentException("The app id or name is empty.", nameof(app));
            _app = app.Trim();
        }

        private string U(IEnumerable<KeyValuePair<string, string?>>? query, params string[] segments)
        {
            var all = new List<string>(segments.Length + 1) { _app };
            all.AddRange(segments);
            return EnluUrl.Build(_base, "v1/apps", all, query);
        }

        private static KeyValuePair<string, string?> Q(string k, string? v) => new KeyValuePair<string, string?>(k, v);

        /// <summary><c>GET|POST /allocations</c></summary>
        public string Allocations() => U(null, "allocations");
        /// <summary><c>GET|DELETE /allocations/:session</c></summary>
        public string Allocation(string sessionId) => U(null, "allocations", sessionId);
        /// <summary><c>GET|PATCH|DELETE /players/:player</c></summary>
        public string Player(string playerId) => U(null, "players", playerId);
        /// <summary><c>PUT /players/:player/data</c></summary>
        public string PlayerData(string playerId) => U(null, "players", playerId, "data");
        /// <summary><c>POST /players/:player/statistics</c></summary>
        public string PlayerStatistics(string playerId) => U(null, "players", playerId, "statistics");
        /// <summary><c>GET|PUT /title-data</c></summary>
        public string TitleData() => U(null, "title-data");
        /// <summary><c>GET /leaderboards/:name?top=N[&amp;around=player][&amp;version=V]</c></summary>
        public string Leaderboard(string name, int top, string? aroundPlayerId, int? version) =>
            U(new[]
            {
                Q("top", top.ToString(CultureInfo.InvariantCulture)),
                Q("around", aroundPlayerId),
                Q("version", version.HasValue ? version.Value.ToString(CultureInfo.InvariantCulture) : null),
            }, "leaderboards", name);
        /// <summary><c>POST /events</c></summary>
        public string Events() => U(null, "events");
        /// <summary><c>POST /functions/:fn</c></summary>
        public string Function(string name) => U(null, "functions", name);
    }
}
