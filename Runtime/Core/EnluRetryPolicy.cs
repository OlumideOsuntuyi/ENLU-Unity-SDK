#nullable enable
using System;

namespace Enlu
{
    /// <summary>How a request may be retried after a failure.</summary>
    public enum EnluRetryMode
    {
        /// <summary>
        /// Retry only when the server rejected the request before acting on it (HTTP 429).
        /// Used for calls that must not run twice: statistics, events, functions, registration.
        /// </summary>
        RejectedOnly = 0,

        /// <summary>Retry on 429 and 503 (used for allocations: a 503 means no server was taken).</summary>
        RejectedOrUnavailable = 1,

        /// <summary>Retry on 429, 503 and network errors. Only for idempotent calls (reads, puts, logins).</summary>
        Idempotent = 2,
    }

    /// <summary>Exponential backoff with jitter, honouring the server's Retry-After.</summary>
    public sealed class EnluRetryPolicy
    {
        /// <summary>Retries after the first attempt (0 disables retries).</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>First backoff delay in seconds; doubles each retry.</summary>
        public double BaseDelaySeconds { get; set; } = 0.5;

        /// <summary>Upper bound for one backoff delay in seconds.</summary>
        public double MaxDelaySeconds { get; set; } = 8;

        /// <summary>Upper bound for a server-requested Retry-After in seconds.</summary>
        public double MaxRetryAfterSeconds { get; set; } = 30;

        private readonly Random _random;

        /// <summary>Creates a policy. Pass a seeded <see cref="Random"/> for deterministic tests.</summary>
        public EnluRetryPolicy(Random? random = null)
        {
            _random = random ?? new Random();
        }

        /// <summary>
        /// Decides whether to retry. <paramref name="status"/> is the HTTP status, or 0 for a network error.
        /// <paramref name="attempt"/> is the number of retries already made.
        /// </summary>
        public bool ShouldRetry(EnluRetryMode mode, int status, int attempt)
        {
            if (attempt >= MaxRetries) return false;
            if (status == 429) return true;
            if (status == 503) return mode != EnluRetryMode.RejectedOnly;
            if (status == 0) return mode == EnluRetryMode.Idempotent;
            return false;
        }

        /// <summary>Delay in seconds before retry number <paramref name="attempt"/> (0-based).</summary>
        public double GetDelaySeconds(int attempt, double? retryAfter)
        {
            var exp = BaseDelaySeconds * Math.Pow(2, Math.Max(0, Math.Min(attempt, 16)));
            double jitter;
            lock (_random) jitter = 0.5 + _random.NextDouble() * 0.5; // 50–100 %
            var backoff = Math.Min(MaxDelaySeconds, exp) * jitter;
            if (retryAfter.HasValue)
            {
                var ra = Math.Min(MaxRetryAfterSeconds, Math.Max(0, retryAfter.Value));
                return Math.Max(ra, backoff);
            }
            return backoff;
        }
    }
}
