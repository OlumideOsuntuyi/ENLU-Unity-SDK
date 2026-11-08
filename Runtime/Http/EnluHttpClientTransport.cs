#nullable enable
#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu
{
    /// <summary>
    /// A transport built on <c>System.Net.Http.HttpClient</c>. Use it for dedicated servers, trusted
    /// backends and tools that run off the main thread. Not available on WebGL (browsers have no
    /// sockets); game clients should keep the default <c>UnityWebRequest</c> transport.
    /// </summary>
    public sealed class EnluHttpClientTransport : IEnluTransport, IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _ownsClient;

        /// <summary>Creates a transport with its own <see cref="HttpClient"/>.</summary>
        public EnluHttpClientTransport()
            : this(new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan }, true)
        {
        }

        /// <summary>Creates a transport over an existing client (its Timeout should be infinite; the SDK times out per request).</summary>
        public EnluHttpClientTransport(HttpClient client, bool ownsClient = false)
        {
            _http = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
        }

        /// <inheritdoc />
        public async Task<EnluHttpResponse> SendAsync(EnluHttpRequest request, CancellationToken cancellationToken)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds))))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            using (var msg = new HttpRequestMessage(new HttpMethod(request.Method), request.Url))
            {
                foreach (var h in request.Headers)
                {
                    if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                    msg.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                if (request.Body != null)
                    msg.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
                try
                {
                    using (var res = await _http.SendAsync(msg, linked.Token).ConfigureAwait(false))
                    {
                        var r = new EnluHttpResponse
                        {
                            Status = (int)res.StatusCode,
                            Body = res.Content == null ? string.Empty : await res.Content.ReadAsStringAsync().ConfigureAwait(false),
                        };
                        foreach (var h in res.Headers) r.Headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
                        if (res.Content != null)
                            foreach (var h in res.Content.Headers) r.Headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
                        return r;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return new EnluHttpResponse { Status = 0, TimedOut = true, NetworkError = "timeout" };
                }
                catch (HttpRequestException e)
                {
                    return new EnluHttpResponse { Status = 0, NetworkError = e.Message };
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_ownsClient) _http.Dispose();
        }
    }
}
#endif
