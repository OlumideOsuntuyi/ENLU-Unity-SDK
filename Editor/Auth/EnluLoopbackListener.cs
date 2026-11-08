#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu.Editor
{
    /// <summary>What a request to the loopback listener turned out to be.</summary>
    public enum EnluCallbackKind
    {
        /// <summary>Not <c>GET /callback</c> (e.g. <c>/favicon.ico</c>): answered with 404 and ignored.</summary>
        NotCallback = 0,
        /// <summary>A callback that is malformed (no code and no error).</summary>
        Malformed = 1,
        /// <summary>A callback whose state does not match this sign-in: possibly forged.</summary>
        StateMismatch = 2,
        /// <summary>The developer approved: <see cref="EnluCallbackResult.Code"/> is set.</summary>
        Approved = 3,
        /// <summary>The developer refused or the server reported an error: <see cref="EnluCallbackResult.Error"/> is set.</summary>
        Denied = 4,
    }

    /// <summary>The parsed browser callback.</summary>
    public sealed class EnluCallbackResult
    {
        /// <summary>What kind of request it was.</summary>
        public EnluCallbackKind Kind { get; }
        /// <summary>The one-time authorization code when approved.</summary>
        public string? Code { get; }
        /// <summary>The OAuth error (e.g. <c>access_denied</c>) when denied.</summary>
        public string? Error { get; }

        /// <summary>Creates a result.</summary>
        public EnluCallbackResult(EnluCallbackKind kind, string? code = null, string? error = null)
        {
            Kind = kind;
            Code = code;
            Error = error;
        }

        /// <inheritdoc />
        public override string ToString() => "Callback(" + Kind + (Error != null ? ", " + Error : "") + ")"; // never the code
    }

    /// <summary>
    /// A one-shot HTTP listener on <c>127.0.0.1</c> for the OAuth redirect (RFC 8252 §7.3). It uses a
    /// raw <see cref="TcpListener"/> on the loopback interface (no URL ACLs needed on Windows), accepts
    /// only <c>GET /callback</c>, answers anything else with 404, and stops after the first callback.
    /// </summary>
    public sealed class EnluLoopbackListener : IDisposable
    {
        /// <summary>Path of the redirect URI.</summary>
        public const string CallbackPath = "/callback";

        private const int MaxRequestBytes = 8192;
        private const int MaxStrayRequests = 50;
        private readonly TcpListener _listener;
        private bool _disposed;

        /// <summary>The port chosen by the OS.</summary>
        public int Port { get; }

        /// <summary><c>http://127.0.0.1:&lt;port&gt;/callback</c>.</summary>
        public string RedirectUri => BuildRedirectUri(Port);

        private EnluLoopbackListener(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>Starts listening on a random free port on <c>127.0.0.1</c> only.</summary>
        public static EnluLoopbackListener Start()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start(4);
            return new EnluLoopbackListener(l);
        }

        /// <summary>Builds the redirect URI for a port.</summary>
        public static string BuildRedirectUri(int port)
        {
            if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + CallbackPath;
        }

        /// <summary>
        /// Waits for the browser's callback. Stray requests get 404 and waiting continues. The first
        /// <c>GET /callback</c> gets a small HTML page and ends the wait, whatever it contains.
        /// Throws <see cref="TimeoutException"/> after <paramref name="timeout"/> and
        /// <see cref="OperationCanceledException"/> on cancellation.
        /// </summary>
        public async Task<EnluCallbackResult> WaitForCallbackAsync(string expectedState, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using (var timeoutCts = new CancellationTokenSource(timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            using (linked.Token.Register(Stop))
            {
                try
                {
                    for (var stray = 0; stray < MaxStrayRequests; stray++)
                    {
                        TcpClient client;
                        try
                        {
                            client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        }
                        catch (Exception) when (linked.IsCancellationRequested)
                        {
                            break;
                        }
                        using (client)
                        {
                            var result = await HandleAsync(client, expectedState, linked.Token).ConfigureAwait(false);
                            if (result != null) return result;
                        }
                    }
                }
                finally
                {
                    Stop();
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (timeoutCts.IsCancellationRequested) throw new TimeoutException("No answer from the browser within " + timeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " minutes.");
                throw new InvalidOperationException("Too many unexpected requests on the sign-in port.");
            }
        }

        private static async Task<EnluCallbackResult?> HandleAsync(TcpClient client, string expectedState, CancellationToken ct)
        {
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;
            var stream = client.GetStream();
            string? requestLine;
            try
            {
                requestLine = await ReadRequestHeadAsync(stream, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }
            if (requestLine == null)
            {
                await WriteAsync(stream, 400, "Bad request", EnluCallbackPage.Plain("Bad request.")).ConfigureAwait(false);
                return null;
            }
            var result = ParseRequestLine(requestLine, expectedState);
            switch (result.Kind)
            {
                case EnluCallbackKind.NotCallback:
                    await WriteAsync(stream, 404, "Not Found", EnluCallbackPage.Plain("Not found.")).ConfigureAwait(false);
                    return null;
                case EnluCallbackKind.Approved:
                    await WriteAsync(stream, 200, "OK", EnluCallbackPage.Html("Signed in to ENLU",
                        "You are signed in. You can close this tab and return to Unity.", true)).ConfigureAwait(false);
                    return result;
                case EnluCallbackKind.Denied:
                    await WriteAsync(stream, 200, "OK", EnluCallbackPage.Html("Sign-in cancelled",
                        "The sign-in was cancelled. You can close this tab and return to Unity.", false)).ConfigureAwait(false);
                    return result;
                case EnluCallbackKind.StateMismatch:
                    await WriteAsync(stream, 400, "Bad Request", EnluCallbackPage.Html("Sign-in failed",
                        "This sign-in link does not belong to the sign-in Unity started. Start again from Unity.", false)).ConfigureAwait(false);
                    return result;
                default:
                    await WriteAsync(stream, 400, "Bad Request", EnluCallbackPage.Html("Sign-in failed",
                        "The answer from the server was incomplete. Start again from Unity.", false)).ConfigureAwait(false);
                    return result;
            }
        }

        /// <summary>Reads the request head (up to 8 KB) and returns its first line, or null if malformed.</summary>
        private static async Task<string?> ReadRequestHeadAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[MaxRequestBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer, total, buffer.Length - total, ct).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                if (text.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0 || text.IndexOf("\n\n", StringComparison.Ordinal) >= 0)
                {
                    var end = text.IndexOf('\n');
                    return end < 0 ? null : text.Substring(0, end).TrimEnd('\r');
                }
            }
            return null;
        }

        /// <summary>
        /// Parses an HTTP request line such as <c>GET /callback?code=…&amp;state=… HTTP/1.1</c>.
        /// Only <c>GET /callback</c> counts; the state is compared in constant time.
        /// </summary>
        public static EnluCallbackResult ParseRequestLine(string requestLine, string expectedState)
        {
            if (string.IsNullOrEmpty(requestLine)) return new EnluCallbackResult(EnluCallbackKind.NotCallback);
            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                return new EnluCallbackResult(EnluCallbackKind.NotCallback);
            if (parts[0] != "GET") return new EnluCallbackResult(EnluCallbackKind.NotCallback);
            var target = parts[1];
            var q = target.IndexOf('?');
            var path = q < 0 ? target : target.Substring(0, q);
            if (path != CallbackPath) return new EnluCallbackResult(EnluCallbackKind.NotCallback);

            Dictionary<string, string> query;
            try
            {
                query = ParseQuery(q < 0 ? string.Empty : target.Substring(q + 1));
            }
            catch (FormatException)
            {
                return new EnluCallbackResult(EnluCallbackKind.Malformed);
            }
            query.TryGetValue("state", out var state);
            if (!EnluPkce.ConstantTimeEquals(state, expectedState)) return new EnluCallbackResult(EnluCallbackKind.StateMismatch);
            if (query.TryGetValue("error", out var error) && error.Length > 0)
                return new EnluCallbackResult(EnluCallbackKind.Denied, null, error.Length > 64 ? error.Substring(0, 64) : error);
            if (query.TryGetValue("code", out var code) && code.Length > 0 && code.Length <= 512)
                return new EnluCallbackResult(EnluCallbackKind.Approved, code);
            return new EnluCallbackResult(EnluCallbackKind.Malformed);
        }

        /// <summary>Parses <c>a=1&amp;b=2</c>; duplicate keys make the query malformed.</summary>
        internal static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (query.Length == 0) return result;
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                var key = Decode(eq < 0 ? pair : pair.Substring(0, eq));
                var value = eq < 0 ? string.Empty : Decode(pair.Substring(eq + 1));
                if (result.ContainsKey(key)) throw new FormatException("Duplicate query parameter.");
                result[key] = value;
            }
            return result;
        }

        private static string Decode(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '%') continue;
                if (i + 2 >= s.Length || !IsHex(s[i + 1]) || !IsHex(s[i + 2])) throw new FormatException("Invalid percent-encoding.");
            }
            try
            {
                return Uri.UnescapeDataString(s.Replace('+', ' '));
            }
            catch (UriFormatException e)
            {
                throw new FormatException(e.Message);
            }
        }

        private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static async Task WriteAsync(Stream stream, int status, string reason, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n" +
                       "Content-Type: text/html; charset=utf-8\r\n" +
                       "Content-Length: " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                       "Cache-Control: no-store\r\n" +
                       "Referrer-Policy: no-referrer\r\n" +
                       "X-Content-Type-Options: nosniff\r\n" +
                       "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
                       "Connection: close\r\n\r\n";
            try
            {
                var h = Encoding.ASCII.GetBytes(head);
                await stream.WriteAsync(h, 0, h.Length).ConfigureAwait(false);
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The browser went away; the result still counts.
            }
        }

        private void Stop()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
            }
        }

        /// <summary>Stops listening.</summary>
        public void Dispose() => Stop();
    }

    /// <summary>The self-contained pages shown in the browser after the redirect.</summary>
    internal static class EnluCallbackPage
    {
        public static string Plain(string message) =>
            "<!doctype html><meta charset=\"utf-8\"><title>ENLU</title><p>" + Escape(message) + "</p>";

        public static string Html(string title, string message, bool ok)
        {
            var accent = ok ? "#1a7f4b" : "#a33a2b";
            return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                   "<title>" + Escape(title) + "</title><style>" +
                   ":root{color-scheme:light dark}body{margin:0;min-height:100vh;display:grid;place-items:center;" +
                   "font:16px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:Canvas;color:CanvasText}" +
                   "main{max-width:28rem;padding:2rem;text-align:center}" +
                   "h1{font-size:1.4rem;margin:0 0 .5rem;color:" + accent + "}p{margin:0;opacity:.85}" +
                   "</style></head><body><main><h1>" + Escape(title) + "</h1><p>" + Escape(message) + "</p></main></body></html>";
        }

        private static string Escape(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
