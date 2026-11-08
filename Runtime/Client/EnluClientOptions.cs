#nullable enable

namespace Enlu
{
    /// <summary>
    /// Plain settings for <see cref="EnluClient"/> and <see cref="EnluServerApi"/>. In Unity you
    /// normally create an <c>EnluSettings</c> asset instead, which produces these.
    /// </summary>
    public sealed class EnluClientOptions
    {
        /// <summary>Control node URL, e.g. <c>https://api.example.com</c>.</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>App id (<c>app_…</c>) or app name.</summary>
        public string App { get; set; } = string.Empty;

        /// <summary>Per-request timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; } = 15;

        /// <summary>Retries for transient failures (429, 503, network errors on idempotent calls).</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>Log one line per request (method, path, status). Never logs headers, bodies or tickets.</summary>
        public bool LogRequests { get; set; }

        /// <summary>Most events kept in the offline queue; older ones are dropped beyond this.</summary>
        public int MaxQueuedEvents { get; set; } = 1000;

        /// <summary>Returns a copy.</summary>
        public EnluClientOptions Clone() => (EnluClientOptions)MemberwiseClone();
    }
}
