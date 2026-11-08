#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Enlu
{
    /// <summary>Result of a login or registration.</summary>
    public sealed class LoginResult
    {
        /// <summary>
        /// The session ticket. The client stores and sends it for you; you rarely need it.
        /// Treat it like a password: never log it, never put it in a URL.
        /// </summary>
        public string SessionTicket { get; private set; } = string.Empty;

        /// <summary>The player's id (<c>pl_…</c>).</summary>
        public string PlayerId { get; private set; } = string.Empty;

        /// <summary>When the ticket expires (UTC). Log in again after this.</summary>
        public DateTime? ExpiresAt { get; private set; }

        /// <summary>True when this call created the account.</summary>
        public bool NewlyCreated { get; private set; }

        /// <summary>Reads the API response.</summary>
        public static LoginResult FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw BadShape("login");
            var r = new LoginResult
            {
                SessionTicket = EnluJson.GetString(o, "sessionTicket") ?? string.Empty,
                PlayerId = EnluJson.GetString(o, "playerId") ?? string.Empty,
                ExpiresAt = EnluJson.ToDateTime(EnluJson.Get(o, "expiresAt")),
                NewlyCreated = EnluJson.GetBool(o, "newlyCreated"),
            };
            if (r.SessionTicket.Length == 0 || r.PlayerId.Length == 0) throw BadShape("login");
            return r;
        }

        /// <summary>Describes the login without the ticket.</summary>
        public override string ToString() => "LoginResult(player " + PlayerId + (NewlyCreated ? ", new" : "") + ")";

        internal static EnluException BadShape(string what) =>
            new EnluException("bad_response", "Unexpected " + what + " response from the server.", 200);
    }

    /// <summary>A statistic value on a player's profile.</summary>
    public sealed class PlayerStatistic
    {
        /// <summary>Current value.</summary>
        public double Value { get; set; }
        /// <summary>Statistic version (increments on every reset).</summary>
        public int Version { get; set; }
    }

    /// <summary>A player account as the API returns it.</summary>
    public sealed class PlayerProfile
    {
        /// <summary>Player id (<c>pl_…</c>).</summary>
        public string Id { get; private set; } = string.Empty;
        /// <summary>Custom id (device or your own id), if any.</summary>
        public string? CustomId { get; private set; }
        /// <summary>Email, if the player registered with one.</summary>
        public string? Email { get; private set; }
        /// <summary>Display name shown on leaderboards.</summary>
        public string? DisplayName { get; private set; }
        /// <summary>True when banned.</summary>
        public bool Banned { get; private set; }
        /// <summary>Ban reason, if any.</summary>
        public string? BanReason { get; private set; }
        /// <summary>Creation time (UTC).</summary>
        public DateTime? CreatedAt { get; private set; }
        /// <summary>Last login (UTC).</summary>
        public DateTime? LastLogin { get; private set; }
        /// <summary>Number of logins.</summary>
        public long LoginCount { get; private set; }
        /// <summary>Current statistics (only filled by <c>GetProfileAsync</c>).</summary>
        public Dictionary<string, PlayerStatistic> Statistics { get; private set; } = new Dictionary<string, PlayerStatistic>(StringComparer.Ordinal);

        /// <summary>Reads the API response.</summary>
        public static PlayerProfile FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw LoginResult.BadShape("player");
            var p = new PlayerProfile
            {
                Id = EnluJson.GetString(o, "id") ?? string.Empty,
                CustomId = EnluJson.GetString(o, "customId"),
                Email = EnluJson.GetString(o, "email"),
                DisplayName = EnluJson.GetString(o, "displayName"),
                Banned = EnluJson.GetBool(o, "banned"),
                BanReason = EnluJson.GetString(o, "banReason"),
                CreatedAt = EnluJson.ToDateTime(EnluJson.Get(o, "createdAt")),
                LastLogin = EnluJson.ToDateTime(EnluJson.Get(o, "lastLogin")),
                LoginCount = EnluJson.GetLong(o, "loginCount"),
            };
            var stats = EnluJson.GetObject(o, "statistics");
            if (stats != null)
            {
                foreach (var kv in stats)
                {
                    var s = EnluJson.AsObject(kv.Value);
                    if (s == null) continue;
                    p.Statistics[kv.Key] = new PlayerStatistic { Value = EnluJson.GetDouble(s, "value"), Version = EnluJson.GetInt(s, "version") };
                }
            }
            return p;
        }

        /// <inheritdoc />
        public override string ToString() => "Player " + Id + (DisplayName != null ? " (" + DisplayName + ")" : "");
    }

    /// <summary>Who can read a player data key.</summary>
    public enum DataVisibility
    {
        /// <summary>Only the player and your servers.</summary>
        Private = 0,
        /// <summary>Also other players (<c>GetPlayerPublicDataAsync</c>).</summary>
        Public = 1,
    }

    /// <summary>Who can write a player data key (server API only).</summary>
    public enum DataAccess
    {
        /// <summary>The player's client may write it.</summary>
        Client = 0,
        /// <summary>Clients can read but only servers can write (e.g. currency, unlocks).</summary>
        ReadOnly = 1,
        /// <summary>Servers only; clients never see it.</summary>
        Internal = 2,
    }

    internal static class EnluEnumText
    {
        public static string ToApi(DataVisibility v) => v == DataVisibility.Public ? "public" : "private";

        public static string ToApi(DataAccess a)
        {
            switch (a)
            {
                case DataAccess.ReadOnly: return "readonly";
                case DataAccess.Internal: return "internal";
                default: return "client";
            }
        }
    }

    /// <summary>One player data key.</summary>
    public sealed class PlayerDataEntry
    {
        /// <summary>The stored value as parsed JSON (dictionary, list, string, long, double, bool or null).</summary>
        public object? Value { get; private set; }
        /// <summary><c>client</c>, <c>readonly</c> or <c>internal</c>.</summary>
        public string Access { get; private set; } = "client";
        /// <summary><c>private</c> or <c>public</c>.</summary>
        public string Visibility { get; private set; } = "private";
        /// <summary>Last write (UTC).</summary>
        public DateTime? UpdatedAt { get; private set; }

        /// <summary>Converts <see cref="Value"/> (see <see cref="EnluJson.ConvertTo{T}(object)"/>).</summary>
        public T GetValue<T>() => EnluJson.ConvertTo<T>(Value);

        /// <summary>The value as JSON text, handy for <c>JsonUtility.FromJson</c>.</summary>
        public string ValueJson => EnluJson.Serialize(Value);

        /// <summary>Reads one entry.</summary>
        public static PlayerDataEntry FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw LoginResult.BadShape("data");
            return new PlayerDataEntry
            {
                Value = EnluJson.Get(o, "value"),
                Access = EnluJson.GetString(o, "access") ?? "client",
                Visibility = EnluJson.GetString(o, "visibility") ?? "private",
                UpdatedAt = EnluJson.ToDateTime(EnluJson.Get(o, "updatedAt")),
            };
        }

        /// <summary>Reads <c>{ "data": { key: entry } }</c>.</summary>
        public static Dictionary<string, PlayerDataEntry> MapFromResponse(object? node)
        {
            var result = new Dictionary<string, PlayerDataEntry>(StringComparer.Ordinal);
            var data = EnluJson.GetObject(EnluJson.AsObject(node), "data");
            if (data == null) return result;
            foreach (var kv in data)
            {
                if (EnluJson.AsObject(kv.Value) != null) result[kv.Key] = FromJson(kv.Value);
            }
            return result;
        }
    }

    /// <summary>One row of a leaderboard.</summary>
    public sealed class LeaderboardEntry
    {
        /// <summary>1-based rank.</summary>
        public int Position { get; set; }
        /// <summary>Player id.</summary>
        public string PlayerId { get; set; } = string.Empty;
        /// <summary>Display name, or null.</summary>
        public string? DisplayName { get; set; }
        /// <summary>Statistic value.</summary>
        public double Value { get; set; }

        /// <inheritdoc />
        public override string ToString() =>
            Position.ToString(CultureInfo.InvariantCulture) + ". " + (DisplayName ?? PlayerId) + " " + Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A page of a leaderboard.</summary>
    public sealed class Leaderboard
    {
        /// <summary>Statistic name.</summary>
        public string Statistic { get; private set; } = string.Empty;
        /// <summary>Statistic version this page is from.</summary>
        public int Version { get; private set; }
        /// <summary>Rows, best first.</summary>
        public List<LeaderboardEntry> Entries { get; private set; } = new List<LeaderboardEntry>();

        /// <summary>Reads the API response.</summary>
        public static Leaderboard FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw LoginResult.BadShape("leaderboard");
            var lb = new Leaderboard
            {
                Statistic = EnluJson.GetString(o, "statistic") ?? string.Empty,
                Version = EnluJson.GetInt(o, "version"),
            };
            var entries = EnluJson.GetArray(o, "entries");
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    var eo = EnluJson.AsObject(e);
                    if (eo == null) continue;
                    lb.Entries.Add(new LeaderboardEntry
                    {
                        Position = EnluJson.GetInt(eo, "position"),
                        PlayerId = EnluJson.GetString(eo, "playerId") ?? string.Empty,
                        DisplayName = EnluJson.GetString(eo, "displayName"),
                        Value = EnluJson.GetDouble(eo, "value"),
                    });
                }
            }
            return lb;
        }
    }

    /// <summary>One statistic value in a data export.</summary>
    public sealed class ExportedStatistic
    {
        /// <summary>Statistic name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Version.</summary>
        public int Version { get; set; }
        /// <summary>Value.</summary>
        public double Value { get; set; }
        /// <summary>Last update (UTC).</summary>
        public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>One event in a data export.</summary>
    public sealed class ExportedEvent
    {
        /// <summary>Event name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Event body (parsed JSON).</summary>
        public object? Body { get; set; }
        /// <summary><c>client</c>, <c>server</c>, <c>script</c> or <c>system</c>.</summary>
        public string Source { get; set; } = string.Empty;
        /// <summary>Timestamp (UTC).</summary>
        public DateTime? Timestamp { get; set; }
    }

    /// <summary>Everything the service stores about the player (data-portability export).</summary>
    public sealed class PlayerExport
    {
        /// <summary>When the export was made (UTC).</summary>
        public DateTime? ExportedAt { get; private set; }
        /// <summary>App id.</summary>
        public string AppId { get; private set; } = string.Empty;
        /// <summary>The profile.</summary>
        public PlayerProfile Profile { get; private set; } = new PlayerProfile();
        /// <summary>All player data keys.</summary>
        public Dictionary<string, PlayerDataEntry> Data { get; private set; } = new Dictionary<string, PlayerDataEntry>(StringComparer.Ordinal);
        /// <summary>All statistic values, every version.</summary>
        public List<ExportedStatistic> Statistics { get; private set; } = new List<ExportedStatistic>();
        /// <summary>All events.</summary>
        public List<ExportedEvent> Events { get; private set; } = new List<ExportedEvent>();
        /// <summary>The complete export as parsed JSON (includes fields this SDK version does not model).</summary>
        public Dictionary<string, object?> Raw { get; private set; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <summary>The complete export as JSON text, e.g. to save it to a file for the player.</summary>
        public string ToJson(bool pretty = true) => EnluJson.Serialize(Raw, pretty);

        /// <summary>Reads the API response.</summary>
        public static PlayerExport FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw LoginResult.BadShape("export");
            var x = new PlayerExport
            {
                Raw = o,
                ExportedAt = EnluJson.ToDateTime(EnluJson.Get(o, "exportedAt")),
                AppId = EnluJson.GetString(o, "appId") ?? string.Empty,
            };
            if (EnluJson.GetObject(o, "profile") is Dictionary<string, object?> profile) x.Profile = PlayerProfile.FromJson(profile);
            x.Data = PlayerDataEntry.MapFromResponse(o);
            var stats = EnluJson.GetArray(o, "statistics");
            if (stats != null)
            {
                foreach (var s in stats)
                {
                    var so = EnluJson.AsObject(s);
                    if (so == null) continue;
                    x.Statistics.Add(new ExportedStatistic
                    {
                        Name = EnluJson.GetString(so, "name") ?? string.Empty,
                        Version = EnluJson.GetInt(so, "version"),
                        Value = EnluJson.GetDouble(so, "value"),
                        UpdatedAt = EnluJson.ToDateTime(EnluJson.Get(so, "updatedAt")),
                    });
                }
            }
            var events = EnluJson.GetArray(o, "events");
            if (events != null)
            {
                foreach (var e in events)
                {
                    var eo = EnluJson.AsObject(e);
                    if (eo == null) continue;
                    x.Events.Add(new ExportedEvent
                    {
                        Name = EnluJson.GetString(eo, "name") ?? string.Empty,
                        Body = EnluJson.Get(eo, "body"),
                        Source = EnluJson.GetString(eo, "source") ?? string.Empty,
                        Timestamp = EnluJson.ToDateTime(EnluJson.Get(eo, "ts")),
                    });
                }
            }
            return x;
        }
    }

    /// <summary>A player event to write (see <c>WriteEventAsync</c> and the batched queue).</summary>
    public sealed class EnluEvent
    {
        /// <summary>Event name: starts with a letter; letters, digits, <c>_.-</c>; up to 64 characters.</summary>
        public string Name { get; }
        /// <summary>Event body (dictionary, list or primitive); up to 16 KB as JSON.</summary>
        public object? Body { get; }

        /// <summary>Creates an event.</summary>
        public EnluEvent(string name, object? body = null)
        {
            if (!EnluIds.IsValidName(name))
                throw new ArgumentException("Event names start with a letter and use letters, digits, _ . - (up to 64): " + name, nameof(name));
            Name = name;
            Body = body;
        }

        internal Dictionary<string, object?> ToJsonObject() => new Dictionary<string, object?>
        {
            ["name"] = Name,
            ["body"] = Body ?? new Dictionary<string, object?>(),
        };
    }
}
