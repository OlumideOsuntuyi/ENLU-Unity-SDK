#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Enlu
{
    /// <summary>
    /// An error from the ENLU API (<c>{ "code", "message", "details" }</c>) or from the network.
    /// </summary>
    /// <remarks>
    /// Common codes: <c>bad_request</c> (400), <c>unauthorized</c> (401, log in again),
    /// <c>forbidden</c>, <c>banned</c>, <c>suspended</c> (403), <c>not_found</c> (404),
    /// <c>conflict</c> (409), <c>rate_limited</c> (429), <c>unavailable</c> (503, e.g. no standby
    /// server yet). The SDK adds <c>network_error</c> and <c>timeout</c> (HttpStatus 0),
    /// <c>bad_response</c> (the server answered with something that is not the expected JSON) and
    /// <c>not_logged_in</c> (a player call was made before logging in).
    /// </remarks>
    public class EnluException : Exception
    {
        /// <summary>Machine-readable error code, e.g. <c>unauthorized</c>.</summary>
        public string Code { get; }

        /// <summary>HTTP status code, or 0 when the request never got an answer.</summary>
        public int HttpStatus { get; }

        /// <summary>Seconds the server asked us to wait (Retry-After), if any.</summary>
        public double? RetryAfter { get; }

        /// <summary>Extra error details from the server (parsed JSON), if any.</summary>
        public object? Details { get; }

        /// <summary>Creates the exception.</summary>
        public EnluException(string code, string message, int httpStatus = 0, double? retryAfter = null, object? details = null, Exception? inner = null)
            : base(message, inner)
        {
            Code = string.IsNullOrEmpty(code) ? "error" : code;
            HttpStatus = httpStatus;
            RetryAfter = retryAfter;
            Details = details;
        }

        /// <summary>True for a network failure or timeout (no HTTP answer).</summary>
        public bool IsNetworkError => HttpStatus == 0 && (Code == "network_error" || Code == "timeout");

        /// <summary>True when the player must log in again (expired or missing session).</summary>
        public bool IsUnauthorized => HttpStatus == 401 || Code == "not_logged_in";

        /// <summary>True when retrying later may succeed (429, 503 or network errors).</summary>
        public bool IsTransient => HttpStatus == 429 || HttpStatus == 503 || IsNetworkError;

        /// <inheritdoc />
        public override string ToString() =>
            "EnluException(" + Code + ", HTTP " + HttpStatus.ToString(CultureInfo.InvariantCulture) + "): " + Message;

        /// <summary>
        /// Builds an exception from an HTTP error response body (<c>{code, message, details}</c>).
        /// A body that is not JSON still produces a useful exception.
        /// </summary>
        public static EnluException FromResponse(int status, string? body, double? retryAfter)
        {
            string code = DefaultCode(status);
            string message = "The server returned HTTP " + status.ToString(CultureInfo.InvariantCulture) + ".";
            object? details = null;
            if (!string.IsNullOrEmpty(body) && EnluJson.TryParse(body!, out var parsed) && parsed is Dictionary<string, object?> o)
            {
                code = EnluJson.GetString(o, "code") ?? code;
                message = EnluJson.GetString(o, "message") ?? message;
                details = EnluJson.Get(o, "details");
            }
            return new EnluException(code, message, status, retryAfter, details);
        }

        /// <summary>Parses a Retry-After header value (delta seconds or an HTTP date).</summary>
        public static double? ParseRetryAfter(string? header, DateTime? nowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            var h = header!.Trim();
            if (double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return seconds < 0 ? 0 : seconds;
            if (DateTime.TryParse(h, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            {
                var delta = (at - (nowUtc ?? DateTime.UtcNow)).TotalSeconds;
                return delta < 0 ? 0 : delta;
            }
            return null;
        }

        private static string DefaultCode(int status)
        {
            switch (status)
            {
                case 400: return "bad_request";
                case 401: return "unauthorized";
                case 403: return "forbidden";
                case 404: return "not_found";
                case 409: return "conflict";
                case 429: return "rate_limited";
                case 503: return "unavailable";
                default: return status >= 500 ? "server_error" : "http_error";
            }
        }
    }
}
