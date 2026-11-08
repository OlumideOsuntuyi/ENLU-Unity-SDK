#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Enlu
{
    /// <summary>URL building helpers. Every path segment and query value is percent-encoded.</summary>
    public static class EnluUrl
    {
        /// <summary>
        /// Normalises a base URL: trims whitespace and trailing slashes, and checks it is absolute http(s).
        /// </summary>
        public static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("The ENLU base URL is empty. Set it in your EnluSettings asset.", nameof(baseUrl));
            var b = baseUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(b, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("The ENLU base URL must start with https:// (or http:// for local testing): " + b, nameof(baseUrl));
            return b;
        }

        /// <summary>Percent-encodes one path segment (so "/" and "?" inside a value cannot change the route).</summary>
        public static string EscapeSegment(string segment)
        {
            if (segment == null) throw new ArgumentNullException(nameof(segment));
            if (segment.Length == 0) throw new ArgumentException("A URL path segment cannot be empty.", nameof(segment));
            if (segment == "." || segment == "..") throw new ArgumentException("A URL path segment cannot be '.' or '..'.", nameof(segment));
            return Uri.EscapeDataString(segment);
        }

        /// <summary>
        /// Joins a base URL and path segments. Segments are escaped; literal route parts can be
        /// passed through <paramref name="literalPrefix"/> (e.g. <c>client/v1</c>).
        /// </summary>
        public static string Build(string baseUrl, string literalPrefix, IEnumerable<string> segments, IEnumerable<KeyValuePair<string, string?>>? query = null)
        {
            var sb = new StringBuilder(NormalizeBaseUrl(baseUrl));
            if (!string.IsNullOrEmpty(literalPrefix))
            {
                sb.Append('/');
                sb.Append(literalPrefix.Trim('/'));
            }
            foreach (var s in segments)
            {
                sb.Append('/');
                sb.Append(EscapeSegment(s));
            }
            AppendQuery(sb, query);
            return sb.ToString();
        }

        /// <summary>Appends <c>?a=1&amp;b=2</c>, skipping null values.</summary>
        public static void AppendQuery(StringBuilder sb, IEnumerable<KeyValuePair<string, string?>>? query)
        {
            if (query == null) return;
            var first = true;
            foreach (var kv in query)
            {
                if (kv.Value == null) continue;
                sb.Append(first ? '?' : '&');
                first = false;
                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kv.Value));
            }
        }
    }
}
