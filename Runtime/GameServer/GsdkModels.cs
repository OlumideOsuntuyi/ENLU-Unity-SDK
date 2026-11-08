#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace Enlu
{
    /// <summary>Lifecycle state a game server reports to its node agent.</summary>
    public enum GameServerState
    {
        /// <summary>Starting up and loading; not ready for players.</summary>
        Initializing = 0,
        /// <summary>Ready and waiting to be allocated to a session.</summary>
        StandingBy = 1,
        /// <summary>Allocated: a match is running.</summary>
        Active = 2,
        /// <summary>Shutting down.</summary>
        Terminating = 3,
    }

    /// <summary>What the node agent tells the server in a heartbeat response.</summary>
    public enum GsdkOperation
    {
        /// <summary>Nothing new; keep going.</summary>
        Continue = 0,
        /// <summary>The server has been allocated to a session.</summary>
        Active = 1,
        /// <summary>Shut down now.</summary>
        Terminate = 2,
    }

    /// <summary>The session a game server was allocated to.</summary>
    public sealed class SessionInfo
    {
        /// <summary>Session id (the match id from the allocation).</summary>
        public string SessionId { get; }

        /// <summary>Metadata passed to the allocation (map, mode…).</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; }

        /// <summary>Player ids the allocation said to expect.</summary>
        public IReadOnlyList<string> InitialPlayers { get; }

        /// <summary>When this server learned about the allocation (UTC).</summary>
        public DateTime AllocatedAtUtc { get; }

        /// <summary>Creates session info.</summary>
        public SessionInfo(string sessionId, IDictionary<string, string>? metadata, IEnumerable<string>? initialPlayers, DateTime? allocatedAtUtc = null)
        {
            SessionId = sessionId ?? string.Empty;
            Metadata = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            InitialPlayers = new List<string>(initialPlayers ?? Array.Empty<string>());
            AllocatedAtUtc = allocatedAtUtc ?? DateTime.UtcNow;
        }

        /// <summary>Reads a metadata value, or <paramref name="fallback"/>.</summary>
        public string? GetMetadata(string key, string? fallback = null) => Metadata.TryGetValue(key, out var v) ? v : fallback;

        /// <inheritdoc />
        public override string ToString() =>
            "Session " + SessionId + " (" + InitialPlayers.Count.ToString(CultureInfo.InvariantCulture) + " initial players, " +
            Metadata.Count.ToString(CultureInfo.InvariantCulture) + " metadata keys)";
    }

    /// <summary>The heartbeat body sent to the node agent.</summary>
    public sealed class GsdkHeartbeatRequest
    {
        /// <summary>Current state.</summary>
        public GameServerState State { get; set; }

        /// <summary>Connected player ids.</summary>
        public List<string> Players { get; set; } = new List<string>();

        /// <summary>True when healthy.</summary>
        public bool Healthy { get; set; } = true;

        /// <summary>The JSON body: <c>{"state","players","health"}</c>.</summary>
        public string ToJson() => EnluJson.Serialize(new Dictionary<string, object?>
        {
            ["state"] = State.ToString(),
            ["players"] = Players,
            ["health"] = Healthy ? "Healthy" : "Unhealthy",
        });
    }

    /// <summary>The node agent's heartbeat response.</summary>
    public sealed class GsdkHeartbeatResponse
    {
        /// <summary>What to do.</summary>
        public GsdkOperation Operation { get; set; }

        /// <summary>Session id once allocated, else null.</summary>
        public string? SessionId { get; set; }

        /// <summary>Session metadata.</summary>
        public Dictionary<string, string> SessionMetadata { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Players the allocation said to expect.</summary>
        public List<string> InitialPlayers { get; set; } = new List<string>();

        /// <summary>Parses the agent's JSON. Unknown operations are read as <see cref="GsdkOperation.Continue"/>.</summary>
        public static GsdkHeartbeatResponse Parse(string json)
        {
            var r = new GsdkHeartbeatResponse();
            if (string.IsNullOrWhiteSpace(json)) return r;
            var o = EnluJson.ParseObject(json);
            var op = EnluJson.GetString(o, "operation");
            if (string.Equals(op, "Active", StringComparison.OrdinalIgnoreCase)) r.Operation = GsdkOperation.Active;
            else if (string.Equals(op, "Terminate", StringComparison.OrdinalIgnoreCase)) r.Operation = GsdkOperation.Terminate;
            else r.Operation = GsdkOperation.Continue;
            r.SessionId = EnluJson.GetString(o, "sessionId");
            var meta = EnluJson.GetObject(o, "sessionMetadata");
            if (meta != null)
            {
                foreach (var kv in meta)
                {
                    if (kv.Value == null) continue;
                    r.SessionMetadata[kv.Key] = kv.Value as string ?? EnluJson.ConvertTo<string>(kv.Value);
                }
            }
            r.InitialPlayers = EnluJson.GetStringList(o, "initialPlayers");
            return r;
        }
    }

    /// <summary>
    /// What the node agent tells a game server process through environment variables
    /// (<c>ENLU_GSDK_URL</c>, <c>ENLU_GSDK_TOKEN</c>, <c>ENLU_INSTANCE_ID</c>, <c>ENLU_PORT_&lt;NAME&gt;</c>, …).
    /// </summary>
    public sealed class GsdkConfig
    {
        /// <summary>Agent GSDK endpoint, e.g. <c>http://172.17.0.1:56001</c>. Null in local mode.</summary>
        public string? GsdkUrl { get; private set; }
        /// <summary>Heartbeat token (secret; never logged).</summary>
        internal string? Token { get; private set; }
        /// <summary>Instance id.</summary>
        public string InstanceId { get; private set; } = string.Empty;
        /// <summary>App id.</summary>
        public string AppId { get; private set; } = string.Empty;
        /// <summary>Workload name.</summary>
        public string Workload { get; private set; } = string.Empty;
        /// <summary>Region of the node.</summary>
        public string Region { get; private set; } = string.Empty;
        /// <summary>Node name.</summary>
        public string Node { get; private set; } = string.Empty;
        /// <summary>Public address players connect to (IP or host name).</summary>
        public string PublicAddress { get; private set; } = string.Empty;

        private readonly Dictionary<string, int> _containerPorts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _publicPorts = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>True when no agent is configured (editor or local testing): the SDK simulates the agent.</summary>
        public bool IsLocal => string.IsNullOrEmpty(GsdkUrl);

        /// <summary>Port names (upper-case, '-' as '_') known from the environment.</summary>
        public IEnumerable<string> PortNames
        {
            get
            {
                var names = new HashSet<string>(_containerPorts.Keys, StringComparer.Ordinal);
                names.UnionWith(_publicPorts.Keys);
                return names;
            }
        }

        /// <summary>Normalises a port name the way the agent does: upper case, '-' becomes '_'.</summary>
        public static string NormalizePortName(string name) => (name ?? string.Empty).Trim().ToUpperInvariant().Replace('-', '_');

        /// <summary>Port the server should listen on inside its container (<c>ENLU_PORT_&lt;NAME&gt;</c>), or <paramref name="fallback"/>.</summary>
        public int GetContainerPort(string name, int fallback = 0) =>
            _containerPorts.TryGetValue(NormalizePortName(name), out var p) ? p : fallback;

        /// <summary>
        /// Public port players connect to (<c>ENLU_HOST_PORT_&lt;NAME&gt;</c>), falling back to the container
        /// port and then <paramref name="fallback"/>.
        /// </summary>
        public int GetPublicPort(string name, int fallback = 0)
        {
            var key = NormalizePortName(name);
            if (_publicPorts.TryGetValue(key, out var p)) return p;
            if (_containerPorts.TryGetValue(key, out p)) return p;
            return fallback;
        }

        /// <summary>Reads the current process environment.</summary>
        public static GsdkConfig FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariables());

        /// <summary>Reads a given environment (for tests).</summary>
        public static GsdkConfig FromEnvironment(IDictionary env)
        {
            var c = new GsdkConfig();
            if (env == null) return c;
            foreach (DictionaryEntry e in env)
            {
                var k = e.Key as string;
                var v = e.Value as string;
                if (k == null || v == null) continue;
                switch (k)
                {
                    case "ENLU_GSDK_URL": c.GsdkUrl = string.IsNullOrWhiteSpace(v) ? null : v.Trim().TrimEnd('/'); break;
                    case "ENLU_GSDK_TOKEN": c.Token = v; break;
                    case "ENLU_INSTANCE_ID": c.InstanceId = v; break;
                    case "ENLU_APP_ID": c.AppId = v; break;
                    case "ENLU_WORKLOAD": c.Workload = v; break;
                    case "ENLU_REGION": c.Region = v; break;
                    case "ENLU_NODE": c.Node = v; break;
                    case "ENLU_PUBLIC_ADDRESS": c.PublicAddress = v; break;
                    default:
                        if (k.StartsWith("ENLU_HOST_PORT_", StringComparison.Ordinal))
                        {
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp) && hp > 0 && hp < 65536)
                                c._publicPorts[k.Substring("ENLU_HOST_PORT_".Length)] = hp;
                        }
                        else if (k.StartsWith("ENLU_PORT_", StringComparison.Ordinal))
                        {
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cp) && cp > 0 && cp < 65536)
                                c._containerPorts[k.Substring("ENLU_PORT_".Length)] = cp;
                        }
                        break;
                }
            }
            return c;
        }

        /// <summary>Describes the config without the token.</summary>
        public override string ToString() =>
            IsLocal ? "local mode (ENLU_GSDK_URL not set)"
                : "instance " + InstanceId + ", workload " + Workload + ", region " + Region + ", agent " + GsdkUrl;
    }
}
