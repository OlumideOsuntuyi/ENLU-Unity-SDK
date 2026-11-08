#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu
{
    /// <summary>
    /// Sends JSON requests through a transport, turns error responses into <see cref="EnluException"/>
    /// and retries transient failures according to <see cref="EnluRetryPolicy"/>.
    /// Never logs headers or bodies (they can hold tickets, passwords and keys).
    /// </summary>
    internal sealed class EnluHttpPipeline
    {
        private readonly IEnluTransport _transport;
        private readonly IEnluScheduler _scheduler;

        public EnluRetryPolicy Retry { get; }
        public int TimeoutSeconds { get; set; }
        public bool LogRequests { get; set; }

        public EnluHttpPipeline(IEnluTransport transport, IEnluScheduler scheduler, EnluRetryPolicy retry, int timeoutSeconds)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            Retry = retry ?? throw new ArgumentNullException(nameof(retry));
            TimeoutSeconds = timeoutSeconds;
        }

        /// <summary>Sends a request and returns the parsed JSON body (null for an empty body).</summary>
        public async Task<object?> SendAsync(string method, string url, object? body, IDictionary<string, string>? headers,
            EnluRetryMode retryMode, CancellationToken cancellationToken)
        {
            var bodyJson = body == null ? null : EnluJson.Serialize(body);
            var attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var req = new EnluHttpRequest
                {
                    Method = method,
                    Url = url,
                    Body = bodyJson,
                    TimeoutSeconds = Math.Max(1, TimeoutSeconds),
                };
                req.Headers["Accept"] = "application/json";
                if (bodyJson != null) req.Headers["Content-Type"] = "application/json";
                if (headers != null) foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;

                EnluHttpResponse res;
                try
                {
                    res = await _transport.SendAsync(req, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // A transport should not throw, but treat it as a network failure if it does.
                    res = new EnluHttpResponse { Status = 0, NetworkError = e.Message };
                }

                if (LogRequests)
                    EnluLog.Debug(method + " " + StripQuery(url) + " -> " + (res.Status == 0 ? "network error" : res.Status.ToString(CultureInfo.InvariantCulture)));

                if (res.Status >= 200 && res.Status < 300)
                {
                    if (string.IsNullOrWhiteSpace(res.Body)) return null;
                    try
                    {
                        return EnluJson.Parse(res.Body);
                    }
                    catch (EnluJsonException e)
                    {
                        throw new EnluException("bad_response", "The server answered with something that is not JSON. Check the base URL.", res.Status, null, null, e);
                    }
                }

                EnluException error;
                if (res.Status == 0)
                {
                    error = res.TimedOut
                        ? new EnluException("timeout", "The request timed out.", 0)
                        : new EnluException("network_error", "Could not reach the server: " + (res.NetworkError ?? "unknown network error"), 0);
                }
                else
                {
                    error = EnluException.FromResponse(res.Status, res.Body, EnluException.ParseRetryAfter(res.GetHeader("retry-after")));
                }

                if (!Retry.ShouldRetry(retryMode, res.Status, attempt)) throw error;
                var delay = Retry.GetDelaySeconds(attempt, error.RetryAfter);
                attempt++;
                if (LogRequests) EnluLog.Debug("retry " + attempt.ToString(CultureInfo.InvariantCulture) + " in " + delay.ToString("0.00", CultureInfo.InvariantCulture) + " s (" + error.Code + ")");
                await _scheduler.Delay(delay, cancellationToken);
            }
        }

        private static string StripQuery(string url)
        {
            var q = url.IndexOf('?');
            return q < 0 ? url : url.Substring(0, q);
        }
    }
}
