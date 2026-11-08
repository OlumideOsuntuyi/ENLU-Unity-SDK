#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Enlu
{
    /// <summary>One log line written by a cloud script (server API results only).</summary>
    public sealed class FunctionLog
    {
        /// <summary>Log level, e.g. <c>info</c>, <c>warn</c>, <c>error</c>.</summary>
        public string Level { get; set; } = string.Empty;
        /// <summary>Message.</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// The result of running a cloud function. A function that throws does not throw here:
    /// <see cref="Ok"/> is false and <see cref="Error"/> says why. Call <see cref="EnsureOk"/> to turn
    /// that into an exception.
    /// </summary>
    /// <typeparam name="T">What <see cref="Result"/> is converted to.</typeparam>
    public sealed class FunctionResult<T>
    {
        /// <summary>True when the function returned normally.</summary>
        public bool Ok { get; private set; }
        /// <summary>The returned value converted to <typeparamref name="T"/> (default when not ok or null).</summary>
        public T Result { get; private set; } = default!;
        /// <summary>The returned value as parsed JSON.</summary>
        public object? RawResult { get; private set; }
        /// <summary>The error message when not ok.</summary>
        public string? Error { get; private set; }
        /// <summary>Run time in milliseconds.</summary>
        public double Ms { get; private set; }
        /// <summary>Script logs (only returned to servers using an API key; empty for players).</summary>
        public List<FunctionLog> Logs { get; private set; } = new List<FunctionLog>();
        /// <summary><c>inline</c> or <c>container</c> (server API only).</summary>
        public string? Backend { get; private set; }

        /// <summary>Throws an <see cref="EnluException"/> with code <c>function_error</c> when not ok; returns <see cref="Result"/>.</summary>
        public T EnsureOk()
        {
            if (!Ok) throw new EnluException("function_error", Error ?? "The function failed.", 200);
            return Result;
        }

        /// <summary>Reads the API response <c>{ ok, result, error, ms }</c>.</summary>
        public static FunctionResult<T> FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw LoginResult.BadShape("function");
            var r = new FunctionResult<T>
            {
                Ok = EnluJson.GetBool(o, "ok"),
                RawResult = EnluJson.Get(o, "result"),
                Error = EnluJson.GetString(o, "error"),
                Ms = EnluJson.GetDouble(o, "ms"),
                Backend = EnluJson.GetString(o, "backend"),
            };
            if (r.Ok && r.RawResult != null)
            {
                try
                {
                    r.Result = EnluJson.ConvertTo<T>(r.RawResult);
                }
                catch (Exception e) when (e is InvalidCastException || e is NotSupportedException || e is OverflowException || e is FormatException || e is ArgumentException)
                {
                    throw new EnluException("bad_response", "The function result cannot be converted to " + typeof(T).Name + ": " + e.Message, 200, null, null, e);
                }
            }
            var logs = EnluJson.GetArray(o, "logs");
            if (logs != null)
            {
                foreach (var l in logs)
                {
                    var lo = EnluJson.AsObject(l);
                    if (lo == null) continue;
                    r.Logs.Add(new FunctionLog { Level = EnluJson.GetString(lo, "level") ?? string.Empty, Message = EnluJson.GetString(lo, "message") ?? string.Empty });
                }
            }
            return r;
        }
    }

    /// <summary>A port of an allocated game server.</summary>
    public sealed class AllocationPort
    {
        /// <summary>Port name from the workload spec, e.g. <c>game</c>.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Public port to connect to.</summary>
        public int Port { get; set; }
        /// <summary><c>tcp</c> or <c>udp</c>.</summary>
        public string Protocol { get; set; } = "tcp";

        /// <inheritdoc />
        public override string ToString() => Name + " " + Port.ToString(CultureInfo.InvariantCulture) + "/" + Protocol;
    }

    /// <summary>A game server (or session instance) allocated for a match.</summary>
    public sealed class SessionAllocation
    {
        /// <summary>Session id. Pass it again to get the same server back (allocation is idempotent per session id).</summary>
        public string SessionId { get; private set; } = string.Empty;
        /// <summary>The instance serving the session.</summary>
        public string InstanceId { get; private set; } = string.Empty;
        /// <summary>Workload name.</summary>
        public string Workload { get; private set; } = string.Empty;
        /// <summary>Region of the server.</summary>
        public string Region { get; private set; } = string.Empty;
        /// <summary>Node name.</summary>
        public string Node { get; private set; } = string.Empty;
        /// <summary>Public address (IP or host name) to connect to; null if the node has no public address.</summary>
        public string? Address { get; private set; }
        /// <summary>Public ports.</summary>
        public List<AllocationPort> Ports { get; private set; } = new List<AllocationPort>();
        /// <summary>When the server was allocated (UTC).</summary>
        public DateTime? AllocatedAt { get; private set; }

        /// <summary>Finds a port by name (case-insensitive), or null.</summary>
        public AllocationPort? GetPort(string name)
        {
            foreach (var p in Ports) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>
        /// The port to connect to: the named one, or the first port when <paramref name="name"/> is null. 0 when missing.
        /// </summary>
        public int GetPortNumber(string? name = null)
        {
            if (name == null) return Ports.Count > 0 ? Ports[0].Port : 0;
            return GetPort(name)?.Port ?? 0;
        }

        /// <summary><c>address:port</c> for the named (or first) port, or null when unknown. IPv6 addresses are bracketed.</summary>
        public string? GetEndpoint(string? portName = null)
        {
            var port = GetPortNumber(portName);
            if (string.IsNullOrEmpty(Address) || port == 0) return null;
            var host = Address!.IndexOf(':') >= 0 && !Address.StartsWith("[", StringComparison.Ordinal) ? "[" + Address + "]" : Address;
            return host + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Reads the API response.</summary>
        public static SessionAllocation FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw LoginResult.BadShape("allocation");
            var a = new SessionAllocation
            {
                SessionId = EnluJson.GetString(o, "sessionId") ?? string.Empty,
                InstanceId = EnluJson.GetString(o, "instanceId") ?? string.Empty,
                Workload = EnluJson.GetString(o, "workload") ?? string.Empty,
                Region = EnluJson.GetString(o, "region") ?? string.Empty,
                Node = EnluJson.GetString(o, "node") ?? string.Empty,
                Address = EnluJson.GetString(o, "address"),
                AllocatedAt = EnluJson.ToDateTime(EnluJson.Get(o, "allocatedAt")),
            };
            var ports = EnluJson.GetArray(o, "ports");
            if (ports != null)
            {
                foreach (var p in ports)
                {
                    var po = EnluJson.AsObject(p);
                    if (po == null) continue;
                    a.Ports.Add(new AllocationPort
                    {
                        Name = EnluJson.GetString(po, "name") ?? string.Empty,
                        Port = EnluJson.GetInt(po, "port"),
                        Protocol = EnluJson.GetString(po, "protocol") ?? "tcp",
                    });
                }
            }
            if (a.SessionId.Length == 0) throw LoginResult.BadShape("allocation");
            return a;
        }

        /// <inheritdoc />
        public override string ToString() => "Session " + SessionId + " on " + (GetEndpoint() ?? "(no public address)") + " [" + Region + "]";
    }

    /// <summary>What to allocate (server API).</summary>
    public sealed class AllocationRequest
    {
        /// <summary>Required: the <c>game-server</c> or <c>session</c> workload name.</summary>
        public string Workload { get; set; } = string.Empty;
        /// <summary>Optional session id (letters, digits, <c>_.:-</c>, up to 128). Reusing one returns the same server.</summary>
        public string? SessionId { get; set; }
        /// <summary>Preferred regions, best first. Empty = any region.</summary>
        public List<string> Regions { get; set; } = new List<string>();
        /// <summary>Player ids expected in the match (sent to the server as initial players).</summary>
        public List<string> Players { get; set; } = new List<string>();
        /// <summary>String metadata passed to the game server (map, mode…), up to 30 keys.</summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        internal Dictionary<string, object?> ToJsonObject()
        {
            if (string.IsNullOrWhiteSpace(Workload)) throw new ArgumentException("AllocationRequest.Workload is required.");
            var o = new Dictionary<string, object?> { ["workload"] = Workload };
            if (!string.IsNullOrEmpty(SessionId)) o["sessionId"] = SessionId;
            if (Regions.Count > 0) o["regions"] = Regions;
            if (Players.Count > 0) o["players"] = Players;
            if (Metadata.Count > 0) o["metadata"] = Metadata;
            return o;
        }
    }
}
