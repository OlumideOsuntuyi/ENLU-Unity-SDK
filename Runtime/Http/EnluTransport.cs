#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu
{
    /// <summary>One HTTP request as the SDK hands it to a transport.</summary>
    public sealed class EnluHttpRequest
    {
        /// <summary>GET, POST, PUT, PATCH or DELETE.</summary>
        public string Method { get; set; } = "GET";

        /// <summary>Absolute URL.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Request headers. May contain secrets (session ticket, API key): never log them.</summary>
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>JSON body, or null for no body.</summary>
        public string? Body { get; set; }

        /// <summary>Timeout in seconds for the whole request.</summary>
        public int TimeoutSeconds { get; set; } = 15;
    }

    /// <summary>One HTTP response. <see cref="Status"/> is 0 when there was no answer (network error or timeout).</summary>
    public sealed class EnluHttpResponse
    {
        /// <summary>HTTP status, or 0 for a network error.</summary>
        public int Status { get; set; }

        /// <summary>Response body text (may be empty).</summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>Response headers with lower-case names.</summary>
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>For Status 0: a short description of the network problem.</summary>
        public string? NetworkError { get; set; }

        /// <summary>For Status 0: true when the request timed out.</summary>
        public bool TimedOut { get; set; }

        /// <summary>Reads a header (case-insensitive) or null.</summary>
        public string? GetHeader(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>
    /// Sends HTTP requests. The Unity default uses <c>UnityWebRequest</c> (works on every platform,
    /// including WebGL). Implement your own to add proxies, certificate pinning or test fakes.
    /// A transport must not throw for HTTP or network errors: return a response with the status
    /// (or 0) instead. It may throw <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public interface IEnluTransport
    {
        /// <summary>Sends one request.</summary>
        Task<EnluHttpResponse> SendAsync(EnluHttpRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Waits between retries. The Unity default counts frames on the main thread so it works on
    /// WebGL, where timers backed by threads are not available.
    /// </summary>
    public interface IEnluScheduler
    {
        /// <summary>Completes after <paramref name="seconds"/> (real time).</summary>
        Task Delay(double seconds, CancellationToken cancellationToken);
    }

    /// <summary>A scheduler built on <see cref="Task.Delay(int, CancellationToken)"/>. For servers, tests and tools (not WebGL).</summary>
    public sealed class EnluTaskScheduler : IEnluScheduler
    {
        /// <inheritdoc />
        public Task Delay(double seconds, CancellationToken cancellationToken) =>
            Task.Delay(TimeSpan.FromSeconds(Math.Max(0, seconds)), cancellationToken);
    }

    /// <summary>
    /// Simple string key-value storage, used to keep the session ticket between game launches.
    /// The Unity default is <c>PlayerPrefs</c>. For real secure storage (iOS Keychain, Android
    /// Keystore) implement this and assign <see cref="EnluClient.SecureStore"/>.
    /// </summary>
    public interface IEnluKeyValueStore
    {
        /// <summary>Reads a value or null.</summary>
        string? Get(string key);
        /// <summary>Writes a value.</summary>
        void Set(string key, string value);
        /// <summary>Removes a value.</summary>
        void Delete(string key);
    }

    /// <summary>A store that keeps values in memory only (lost when the game quits).</summary>
    public sealed class EnluMemoryStore : IEnluKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
        /// <inheritdoc />
        public void Set(string key, string value) => _values[key] = value;
        /// <inheritdoc />
        public void Delete(string key) => _values.Remove(key);
    }
}
