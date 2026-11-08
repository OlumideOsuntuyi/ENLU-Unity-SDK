#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu.Editor
{
    /// <summary>
    /// The ENLU management API (<c>/v1/…</c>, the same routes the dashboard uses) called as the
    /// signed-in developer. Results are parsed JSON (see <see cref="EnluJson"/>). Calls the developer's
    /// team role does not allow fail with HTTP 403 (<see cref="EnluException.HttpStatus"/>).
    /// </summary>
    public sealed class EnluDashboardApi
    {
        private readonly EnluEditorAuth _auth;

        /// <summary>Wraps a signed-in session.</summary>
        public EnluDashboardApi(EnluEditorAuth auth)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        }

        private static string A(string app) => "/v1/apps/" + EnluUrl.EscapeSegment(app);
        private static string S(string segment) => EnluUrl.EscapeSegment(segment);

        private async Task<Dictionary<string, object?>> Obj(string method, string path, object? body, CancellationToken ct) =>
            EnluJson.AsObject(await _auth.SendAsync(method, path, body, ct)) ?? new Dictionary<string, object?>();

        private async Task<List<Dictionary<string, object?>>> List(string path, string key, CancellationToken ct)
        {
            var list = new List<Dictionary<string, object?>>();
            var arr = EnluJson.GetArray(await Obj("GET", path, null, ct), key);
            if (arr != null) foreach (var x in arr) if (x is Dictionary<string, object?> o) list.Add(o);
            return list;
        }

        /// <summary><c>GET /v1/accounts/me</c>: name, plan, status, limits, apps, members.</summary>
        public Task<Dictionary<string, object?>> GetAccountAsync(CancellationToken ct = default) => Obj("GET", "/v1/accounts/me", null, ct);

        /// <summary>The credit balance, or null when the developer's role may not see billing (403).</summary>
        public async Task<double?> GetBalanceAsync(CancellationToken ct = default)
        {
            try
            {
                var o = await Obj("GET", "/v1/accounts/me/ledger?limit=1", null, ct);
                return EnluJson.IsNumber(EnluJson.Get(o, "balance")) ? EnluJson.GetDouble(o, "balance") : (double?)null;
            }
            catch (EnluException e) when (e.HttpStatus == 403)
            {
                return null;
            }
        }

        /// <summary><c>GET /v1/apps</c>.</summary>
        public Task<List<Dictionary<string, object?>>> ListAppsAsync(CancellationToken ct = default) => List("/v1/apps", "apps", ct);

        /// <summary>
        /// <c>POST /v1/apps</c>. The result's <c>serverKey</c> is shown once by the server: display it
        /// to the developer and never store it in the project.
        /// </summary>
        public Task<Dictionary<string, object?>> CreateAppAsync(string name, string kind, CancellationToken ct = default) =>
            Obj("POST", "/v1/apps", new Dictionary<string, object?> { ["name"] = name, ["kind"] = kind }, ct);

        /// <summary><c>GET /v1/apps/:app</c>.</summary>
        public Task<Dictionary<string, object?>> GetAppAsync(string app, CancellationToken ct = default) => Obj("GET", A(app), null, ct);

        /// <summary><c>GET /v1/apps/:app/workloads</c> (each with its live <c>instances</c>).</summary>
        public Task<List<Dictionary<string, object?>>> ListWorkloadsAsync(string app, CancellationToken ct = default) =>
            List(A(app) + "/workloads", "workloads", ct);

        /// <summary><c>GET /v1/apps/:app/statistics</c>.</summary>
        public Task<List<Dictionary<string, object?>>> ListStatisticsAsync(string app, CancellationToken ct = default) =>
            List(A(app) + "/statistics", "statistics", ct);

        /// <summary><c>POST /v1/apps/:app/statistics</c> (defines or updates a statistic).</summary>
        public Task<Dictionary<string, object?>> DefineStatisticAsync(string app, string name, string aggregation, string resetPeriod, string sort,
            bool clientWritable, CancellationToken ct = default) =>
            Obj("POST", A(app) + "/statistics", new Dictionary<string, object?>
            {
                ["name"] = name, ["aggregation"] = aggregation, ["resetPeriod"] = resetPeriod, ["sort"] = sort, ["clientWritable"] = clientWritable,
            }, ct);

        /// <summary><c>GET /v1/apps/:app/title-data</c>: <c>{ data, internalKeys }</c>.</summary>
        public Task<Dictionary<string, object?>> GetTitleDataAsync(string app, CancellationToken ct = default) => Obj("GET", A(app) + "/title-data", null, ct);

        /// <summary>
        /// Saves title data from an edited object: keys missing from <paramref name="edited"/> are
        /// deleted, internal keys stay internal and public keys stay public.
        /// </summary>
        public async Task SaveTitleDataAsync(string app, Dictionary<string, object?> original, ICollection<string> internalKeys,
            Dictionary<string, object?> edited, CancellationToken ct = default)
        {
            var pub = new Dictionary<string, object?>(StringComparer.Ordinal);
            var priv = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in edited) (internalKeys.Contains(kv.Key) ? priv : pub)[kv.Key] = kv.Value;
            foreach (var k in original.Keys)
                if (!edited.ContainsKey(k)) (internalKeys.Contains(k) ? priv : pub)[k] = null;
            if (pub.Count > 0) await Obj("PUT", A(app) + "/title-data", new Dictionary<string, object?> { ["data"] = pub, ["internal"] = false }, ct);
            if (priv.Count > 0) await Obj("PUT", A(app) + "/title-data", new Dictionary<string, object?> { ["data"] = priv, ["internal"] = true }, ct);
        }

        /// <summary><c>GET /v1/apps/:app/scripts</c>: <c>{ liveRevision, revisions }</c>.</summary>
        public Task<Dictionary<string, object?>> GetScriptsAsync(string app, CancellationToken ct = default) => Obj("GET", A(app) + "/scripts", null, ct);

        /// <summary><c>POST /v1/apps/:app/scripts</c>: saves and publishes a revision.</summary>
        public Task<Dictionary<string, object?>> PushScriptAsync(string app, string source, string note, CancellationToken ct = default) =>
            Obj("POST", A(app) + "/scripts", new Dictionary<string, object?> { ["source"] = source, ["note"] = note }, ct);

        /// <summary><c>POST /v1/apps/:app/functions/:fn</c>: <c>{ ok, result, error, logs, ms }</c>.</summary>
        public Task<Dictionary<string, object?>> RunFunctionAsync(string app, string function, object? args, CancellationToken ct = default) =>
            Obj("POST", A(app) + "/functions/" + S(function), new Dictionary<string, object?> { ["args"] = args ?? new Dictionary<string, object?>() }, ct);

        /// <summary>Builds a <c>game-server</c> workload spec.</summary>
        public static Dictionary<string, object?> GameServerSpec(string image, string portName, int containerPort, string protocol,
            int standby, int maxServers, double cpu, int memoryMB) => new Dictionary<string, object?>
        {
            ["kind"] = "game-server",
            ["runtime"] = "docker",
            ["image"] = image,
            ["ports"] = new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = portName, ["container"] = containerPort, ["protocol"] = protocol, ["public"] = true },
            },
            ["resources"] = new Dictionary<string, object?> { ["cpu"] = cpu, ["memoryMB"] = memoryMB },
            ["fleet"] = new Dictionary<string, object?> { ["standby"] = standby, ["maxServers"] = maxServers },
        };

        /// <summary>
        /// Creates the workload, or updates its spec when one with that name exists
        /// (<c>POST /v1/apps/:app/workloads</c> or <c>PUT /v1/apps/:app/workloads/:name</c>).
        /// </summary>
        public async Task<Dictionary<string, object?>> CreateOrUpdateWorkloadAsync(string app, string name, Dictionary<string, object?> spec,
            CancellationToken ct = default)
        {
            var exists = true;
            try
            {
                await Obj("GET", A(app) + "/workloads/" + S(name), null, ct);
            }
            catch (EnluException e) when (e.HttpStatus == 404)
            {
                exists = false;
            }
            return exists
                ? await Obj("PUT", A(app) + "/workloads/" + S(name), new Dictionary<string, object?> { ["spec"] = spec }, ct)
                : await Obj("POST", A(app) + "/workloads", new Dictionary<string, object?> { ["name"] = name, ["spec"] = spec }, ct);
        }

        /// <summary><c>POST /v1/apps/:app/workloads/:name/scale</c> with <c>{ standby, maxServers }</c>.</summary>
        public Task<Dictionary<string, object?>> ScaleFleetAsync(string app, string name, int standby, int? maxServers, CancellationToken ct = default)
        {
            var body = new Dictionary<string, object?> { ["standby"] = standby };
            if (maxServers.HasValue) body["maxServers"] = maxServers.Value;
            return Obj("POST", A(app) + "/workloads/" + S(name) + "/scale", body, ct);
        }

        /// <summary>Counts live instances of a workload view by state (running, standby, allocated…).</summary>
        public static Dictionary<string, int> CountInstances(Dictionary<string, object?> workload)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var arr = EnluJson.GetArray(workload, "instances");
            if (arr == null) return counts;
            foreach (var i in arr)
            {
                var state = EnluJson.GetString(EnluJson.AsObject(i), "state") ?? "unknown";
                counts[state] = counts.TryGetValue(state, out var n) ? n + 1 : 1;
            }
            return counts;
        }
    }
}
