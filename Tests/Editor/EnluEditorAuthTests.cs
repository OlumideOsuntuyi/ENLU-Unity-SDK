#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Enlu.Editor;
using NUnit.Framework;

namespace Enlu.Tests
{
    /// <summary>Runs async code with no SynchronizationContext so blocking on it cannot deadlock (Unity or dotnet).</summary>
    internal static class Sync
    {
        public static T Run<T>(Func<Task<T>> f)
        {
            var prev = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                return Task.Run(f).GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prev);
            }
        }

        public static void Run(Func<Task> f) => Run(async () =>
        {
            await f();
            return true;
        });
    }

    /// <summary>A scripted ENLU server for the auth manager (no network).</summary>
    internal sealed class FakeOAuthServer : HttpMessageHandler
    {
        public readonly List<string> Log = new List<string>();
        public string ValidAccess = "enlu_at_1";
        public string ValidRefresh = "enlu_rt_1";
        public readonly HashSet<string> SpentRefresh = new HashSet<string>();
        public bool Revoked;
        public int Refreshes;
        public int Issued = 1;

        private static HttpResponseMessage Json(int status, string body) =>
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public string TokenJson() =>
            "{\"access_token\":\"" + ValidAccess + "\",\"token_type\":\"Bearer\",\"expires_in\":3600,\"refresh_token\":\"" + ValidRefresh +
            "\",\"refresh_expires_in\":2592000,\"user\":{\"id\":\"usr_1\",\"email\":\"dev@example.com\",\"name\":\"Dev\",\"role\":\"developer\"}," +
            "\"account\":{\"id\":\"acc_1\",\"name\":\"Studio\"}}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync();
            lock (Log) Log.Add(request.Method + " " + path);
            if (path == "/oauth/token")
            {
                var form = Enlu.Editor.EnluLoopbackListener.ParseQuery(body);
                if (form["grant_type"] == "refresh_token")
                {
                    lock (this)
                    {
                        Refreshes++;
                        var rt = form["refresh_token"];
                        if (Revoked || SpentRefresh.Contains(rt) || rt != ValidRefresh)
                        {
                            Revoked = true;
                            return Json(400, "{\"code\":\"invalid_grant\",\"message\":\"This sign-in was ended for safety because an old token was used. Sign in again.\"}");
                        }
                        SpentRefresh.Add(rt);
                        Issued++;
                        ValidAccess = "enlu_at_" + Issued;
                        ValidRefresh = "enlu_rt_" + Issued;
                        return Json(200, TokenJson());
                    }
                }
                if (form["grant_type"] == "authorization_code") return Json(200, TokenJson());
                return Json(400, "{\"code\":\"unsupported_grant_type\",\"message\":\"no\"}");
            }
            if (path == "/oauth/revoke")
            {
                Revoked = true;
                return Json(200, "{\"ok\":true}");
            }
            var auth = request.Headers.Authorization?.ToString() ?? string.Empty;
            if (Revoked || auth != "Bearer " + ValidAccess)
                return Json(401, "{\"code\":\"token_expired\",\"message\":\"The access token has expired. Refresh it or sign in again.\"}");
            if (path == "/v1/accounts/me/ledger") return Json(403, "{\"code\":\"forbidden\",\"message\":\"Your team role (developer) cannot read billing.\"}");
            if (path == "/v1/apps") return Json(200, "{\"apps\":[{\"name\":\"my-game\",\"kind\":\"game\",\"players\":3}]}");
            return Json(404, "{\"code\":\"not_found\",\"message\":\"No such endpoint.\"}");
        }
    }

    public sealed class EnluEditorAuthTests
    {
        private const string Base = "https://enlu.example.com";

        /* --------------------------------- PKCE --------------------------------- */

        [Test]
        public void PkceMatchesTheRfc7636Example()
        {
            // RFC 7636 Appendix B: the verifier is base64url of these 32 octets.
            var octets = new byte[] { 116, 24, 223, 180, 151, 153, 224, 37, 79, 250, 96, 125, 216, 173, 187, 186, 22, 212, 37, 77, 105, 214, 191, 240, 91, 88, 5, 88, 83, 132, 141, 121 };
            Assert.That(EnluPkce.Base64Url(octets), Is.EqualTo("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
            Assert.That(EnluPkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"), Is.EqualTo("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        }

        [Test]
        public void VerifiersAndStatesAreRandomAndUrlSafe()
        {
            var seen = new HashSet<string>();
            for (var i = 0; i < 50; i++)
            {
                var v = EnluPkce.CreateVerifier();
                var s = EnluPkce.CreateState();
                Assert.That(v.Length, Is.EqualTo(43));
                Assert.That(s.Length, Is.EqualTo(22));
                Assert.That(v, Does.Match("^[A-Za-z0-9_-]+$"));
                Assert.That(s, Does.Match("^[A-Za-z0-9_-]+$"));
                Assert.That(EnluPkce.ChallengeFor(v).Length, Is.EqualTo(43));
                Assert.That(seen.Add(v) && seen.Add(s), Is.True);
            }
        }

        [Test]
        public void ConstantTimeEqualsCompares()
        {
            Assert.That(EnluPkce.ConstantTimeEquals("abc", "abc"), Is.True);
            Assert.That(EnluPkce.ConstantTimeEquals("abc", "abd"), Is.False);
            Assert.That(EnluPkce.ConstantTimeEquals("abc", "abcd"), Is.False);
            Assert.That(EnluPkce.ConstantTimeEquals("", ""), Is.True);
            Assert.That(EnluPkce.ConstantTimeEquals(null, "a"), Is.False);
            Assert.That(EnluPkce.ConstantTimeEquals(null, null), Is.False);
        }

        /* ------------------------------- callback parsing ------------------------------- */

        [Test]
        public void ParsesAnApprovedCallback()
        {
            var r = EnluLoopbackListener.ParseRequestLine("GET /callback?code=enlu_oc_abc%2B1&state=st-1 HTTP/1.1", "st-1");
            Assert.That(r.Kind, Is.EqualTo(EnluCallbackKind.Approved));
            Assert.That(r.Code, Is.EqualTo("enlu_oc_abc+1"));
            Assert.That(r.ToString(), Does.Not.Contain("enlu_oc"));
        }

        [Test]
        public void ParsesADeniedCallback()
        {
            var r = EnluLoopbackListener.ParseRequestLine("GET /callback?error=access_denied&state=st-1 HTTP/1.1", "st-1");
            Assert.That(r.Kind, Is.EqualTo(EnluCallbackKind.Denied));
            Assert.That(r.Error, Is.EqualTo("access_denied"));
        }

        [TestCase("GET /callback?code=c&state=other HTTP/1.1")]
        [TestCase("GET /callback?code=c HTTP/1.1")]
        [TestCase("GET /callback?error=access_denied&state=st-12 HTTP/1.1")]
        public void RejectsAWrongOrMissingState(string line)
        {
            Assert.That(EnluLoopbackListener.ParseRequestLine(line, "st-1").Kind, Is.EqualTo(EnluCallbackKind.StateMismatch));
        }

        [TestCase("GET /favicon.ico HTTP/1.1")]
        [TestCase("GET / HTTP/1.1")]
        [TestCase("GET /callback/x?code=c&state=st-1 HTTP/1.1")]
        [TestCase("GET /CALLBACK?code=c&state=st-1 HTTP/1.1")]
        [TestCase("POST /callback?code=c&state=st-1 HTTP/1.1")]
        [TestCase("HEAD /callback?code=c&state=st-1 HTTP/1.1")]
        [TestCase("GET /callback?code=c&state=st-1")]
        [TestCase("GET  /callback?code=c&state=st-1 HTTP/1.1")]
        [TestCase("\u0016\u0003\u0001 binary")]
        [TestCase("")]
        public void IgnoresAnythingButGetCallback(string line)
        {
            Assert.That(EnluLoopbackListener.ParseRequestLine(line, "st-1").Kind, Is.EqualTo(EnluCallbackKind.NotCallback));
        }

        [TestCase("GET /callback?state=st-1 HTTP/1.1")]
        [TestCase("GET /callback?code=&state=st-1 HTTP/1.1")]
        [TestCase("GET /callback?code=a&code=b&state=st-1 HTTP/1.1")]
        [TestCase("GET /callback?code=%ZZ&state=st-1 HTTP/1.1")]
        public void FlagsMalformedCallbacks(string line)
        {
            Assert.That(EnluLoopbackListener.ParseRequestLine(line, "st-1").Kind, Is.EqualTo(EnluCallbackKind.Malformed));
        }

        [Test]
        public void BuildsRedirectAndAuthorizeUrls()
        {
            Assert.That(EnluLoopbackListener.BuildRedirectUri(53123), Is.EqualTo("http://127.0.0.1:53123/callback"));
            Assert.Throws<ArgumentOutOfRangeException>(() => EnluLoopbackListener.BuildRedirectUri(0));
            using (var http = new HttpClient())
            {
                var url = new EnluOAuthClient(Base + "/", http).BuildAuthorizeUrl("http://127.0.0.1:53123/callback", "st-1", "E9Melhoa2OwvRrype9Ny91TbsO5DrgUBYWJRRe3PKOM");
                Assert.That(url, Is.EqualTo(Base + "/oauth/authorize?client_id=enlu-unity-editor&redirect_uri=http%3A%2F%2F127.0.0.1%3A53123%2Fcallback" +
                                            "&state=st-1&code_challenge=E9Melhoa2OwvRrype9Ny91TbsO5DrgUBYWJRRe3PKOM&code_challenge_method=S256"));
            }
        }

        private static string RawRequest(int port, string requestLine)
        {
            using (var c = new TcpClient())
            {
                c.Connect(IPAddress.Loopback, port);
                var s = c.GetStream();
                var bytes = Encoding.ASCII.GetBytes(requestLine + "\r\nHost: 127.0.0.1\r\n\r\n");
                s.Write(bytes, 0, bytes.Length);
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        [Test]
        public void LoopbackListenerAnswersStraysThenTheCallback()
        {
            using (var listener = EnluLoopbackListener.Start())
            {
                Assert.That(listener.RedirectUri, Is.EqualTo("http://127.0.0.1:" + listener.Port + "/callback"));
                var wait = Task.Run(() => listener.WaitForCallbackAsync("st-1", TimeSpan.FromSeconds(20), CancellationToken.None));
                var stray = RawRequest(listener.Port, "GET /favicon.ico HTTP/1.1");
                Assert.That(stray, Does.StartWith("HTTP/1.1 404"));
                Assert.That(wait.IsCompleted, Is.False);
                var page = RawRequest(listener.Port, "GET /callback?code=xyz&state=st-1 HTTP/1.1");
                Assert.That(page, Does.StartWith("HTTP/1.1 200"));
                Assert.That(page, Does.Contain("Referrer-Policy: no-referrer"));
                Assert.That(page, Does.Contain("return to Unity"));
                var result = wait.GetAwaiter().GetResult();
                Assert.That(result.Kind, Is.EqualTo(EnluCallbackKind.Approved));
                Assert.That(result.Code, Is.EqualTo("xyz"));
                Assert.Throws<SocketException>(() => RawRequest(listener.Port, "GET /callback HTTP/1.1"), "the listener is closed after one callback");
            }
        }

        [Test]
        public void LoopbackListenerTimesOutAndCancels()
        {
            using (var listener = EnluLoopbackListener.Start())
            {
                Assert.Throws<TimeoutException>(() => Sync.Run(() => listener.WaitForCallbackAsync("s", TimeSpan.FromMilliseconds(200), CancellationToken.None)));
            }
            using (var listener = EnluLoopbackListener.Start())
            using (var cts = new CancellationTokenSource(200))
            {
                Assert.That(() => Sync.Run(() => listener.WaitForCallbackAsync("s", TimeSpan.FromMinutes(5), cts.Token)), Throws.InstanceOf<OperationCanceledException>());
            }
        }

        /* --------------------------------- stores --------------------------------- */

        [Test]
        public void MemoryCredentialStoreRoundTrips()
        {
            var store = new EnluMemoryCredentialStore();
            Assert.That(store.Read("h"), Is.Null);
            store.Write("h", "secret");
            store.Write("h2", "other");
            Assert.That(store.Read("h"), Is.EqualTo("secret"));
            store.Write("h", "rotated");
            Assert.That(store.Read("h"), Is.EqualTo("rotated"));
            store.Delete("h");
            store.Delete("h");
            Assert.That(store.Read("h"), Is.Null);
            Assert.That(store.Read("h2"), Is.EqualTo("other"));
            Assert.That(store.IsPersistent, Is.False);
        }

        [Test]
        public void HostKeysIdentifyServers()
        {
            Assert.That(EnluCredentialStores.HostKey("https://API.Example.com/"), Is.EqualTo("api.example.com"));
            Assert.That(EnluCredentialStores.HostKey("https://api.example.com:443"), Is.EqualTo("api.example.com"));
            Assert.That(EnluCredentialStores.HostKey("http://127.0.0.1:7700"), Is.EqualTo("127.0.0.1:7700"));
            Assert.Throws<ArgumentException>(() => EnluCredentialStores.HostKey("not a url"));
        }

        [Test]
        public void DpapiStoreRoundTripsOnWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Assert.Ignore("DPAPI is Windows only.");
            var dir = Path.Combine(Path.GetTempPath(), "enlu-dpapi-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new EnluDpapiCredentialStore(dir);
                store.Write("api.example.com", "enlu_rt_secret");
                var files = Directory.GetFiles(dir);
                Assert.That(files.Length, Is.EqualTo(1));
                Assert.That(Path.GetFileName(files[0]), Does.Match("^[0-9a-f]{64}\\.bin$"), "the file name does not reveal the host");
                Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(files[0])), Does.Not.Contain("enlu_rt_secret"), "encrypted at rest");
                Assert.That(store.Read("api.example.com"), Is.EqualTo("enlu_rt_secret"));
                Assert.That(store.Read("other.example.com"), Is.Null);
                store.Write("api.example.com", "enlu_rt_rotated");
                Assert.That(store.Read("api.example.com"), Is.EqualTo("enlu_rt_rotated"));
                File.WriteAllBytes(files[0], new byte[] { 1, 2, 3 });
                Assert.That(store.Read("api.example.com"), Is.Null, "a corrupted blob reads as signed out");
                store.Delete("api.example.com");
                Assert.That(Directory.GetFiles(dir), Is.Empty);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        /* ------------------------------ session manager ------------------------------ */

        private static (EnluEditorAuth Auth, FakeOAuthServer Server, EnluMemoryCredentialStore Store, EnluMemoryTokenCache Cache) SignedIn()
        {
            var server = new FakeOAuthServer();
            var store = new EnluMemoryCredentialStore();
            var cache = new EnluMemoryTokenCache();
            var auth = new EnluEditorAuth(Base, store, cache, new HttpClient(server));
            // Simulate a completed browser sign-in: the stored refresh token restores the session.
            store.Write(auth.HostKey, server.ValidRefresh);
            Assert.That(Sync.Run(() => auth.TryRestoreAsync()), Is.True);
            server.Log.Clear();
            return (auth, server, store, cache);
        }

        [Test]
        public void RestoreRotatesTheRefreshToken()
        {
            var (auth, server, store, cache) = SignedIn();
            Assert.That(auth.Status, Is.EqualTo(EnluAuthStatus.SignedIn));
            Assert.That(auth.User!.Email, Is.EqualTo("dev@example.com"));
            Assert.That(auth.Account!.Name, Is.EqualTo("Studio"));
            Assert.That(store.Read(auth.HostKey), Is.EqualTo("enlu_rt_2"), "the rotated token replaced the spent one");
            Assert.That(cache.Load(auth.HostKey), Does.Contain("enlu_at_2"));
            Assert.That(cache.Load(auth.HostKey), Does.Not.Contain("enlu_rt_"), "the cache never holds the refresh token");
        }

        [Test]
        public void CachedSessionSurvivesAReload()
        {
            var (auth, server, store, cache) = SignedIn();
            var reloaded = new EnluEditorAuth(Base, store, cache, new HttpClient(server));
            Assert.That(reloaded.Status, Is.EqualTo(EnluAuthStatus.SignedIn));
            var apps = Sync.Run(() => reloaded.SendAsync("GET", "/v1/apps"));
            Assert.That(EnluJson.GetArray(EnluJson.AsObject(apps), "apps")!.Count, Is.EqualTo(1));
            Assert.That(server.Refreshes, Is.EqualTo(1), "no refresh needed while the access token is valid");
        }

        [Test]
        public void RefreshesOnceAfter401()
        {
            var (auth, server, store, _) = SignedIn();
            server.ValidAccess = "enlu_at_changed_on_server"; // the current access token stops working
            var r = Sync.Run(() => auth.SendAsync("GET", "/v1/apps"));
            Assert.That(r, Is.Not.Null);
            Assert.That(server.Log, Is.EqualTo(new List<string> { "GET /v1/apps", "POST /oauth/token", "GET /v1/apps" }));
            Assert.That(store.Read(auth.HostKey), Is.EqualTo(server.ValidRefresh));
        }

        [Test]
        public void RefreshesShortlyBeforeExpiry()
        {
            var (auth, server, _, _) = SignedIn();
            auth.SetAccessExpiryForTests(DateTime.UtcNow.AddSeconds(20));
            Sync.Run(() => auth.SendAsync("GET", "/v1/apps"));
            Assert.That(server.Log, Is.EqualTo(new List<string> { "POST /oauth/token", "GET /v1/apps" }));
        }

        [Test]
        public void ConcurrentRequestsShareOneRefresh()
        {
            var (auth, server, _, _) = SignedIn();
            auth.SetAccessExpiryForTests(DateTime.UtcNow.AddSeconds(-1));
            var refreshesBefore = server.Refreshes;
            Sync.Run(async () =>
            {
                var tasks = new List<Task>();
                for (var i = 0; i < 8; i++) tasks.Add(auth.SendAsync("GET", "/v1/apps"));
                await Task.WhenAll(tasks);
                return true;
            });
            Assert.That(server.Refreshes - refreshesBefore, Is.EqualTo(1), "a second refresh with the spent token would end the sign-in");
            Assert.That(server.Revoked, Is.False);
            Assert.That(auth.IsSignedIn, Is.True);
        }

        [Test]
        public void ReuseDetectionSignsOut()
        {
            var (auth, server, store, cache) = SignedIn();
            var changes = 0;
            auth.Changed += () => changes++;
            server.Revoked = true; // e.g. a copied refresh token was used elsewhere
            var e = Assert.Throws<EnluException>(() => Sync.Run(() => auth.SendAsync("GET", "/v1/apps")));
            Assert.That(e!.Code, Is.EqualTo("signed_out"));
            Assert.That(auth.Status, Is.EqualTo(EnluAuthStatus.SignedOut));
            Assert.That(store.Read(auth.HostKey), Is.Null);
            Assert.That(cache.Load(auth.HostKey), Is.Null);
            Assert.That(auth.LastMessage, Does.Contain("old token"));
            Assert.That(changes, Is.GreaterThan(0));
        }

        [Test]
        public void ForbiddenIsReportedWithoutSigningOut()
        {
            var (auth, _, _, _) = SignedIn();
            var e = Assert.Throws<EnluException>(() => Sync.Run(() => auth.SendAsync("GET", "/v1/accounts/me/ledger")));
            Assert.That(e!.HttpStatus, Is.EqualTo(403));
            Assert.That(auth.IsSignedIn, Is.True);
            Assert.That(Sync.Run(() => new EnluDashboardApi(auth).GetBalanceAsync()), Is.Null, "balance is hidden for roles without billing access");
        }

        [Test]
        public void SignOutRevokesAndForgets()
        {
            var (auth, server, store, cache) = SignedIn();
            Sync.Run(() => auth.SignOutAsync());
            Assert.That(server.Log, Is.EqualTo(new List<string> { "POST /oauth/revoke" }));
            Assert.That(server.Revoked, Is.True);
            Assert.That(auth.Status, Is.EqualTo(EnluAuthStatus.SignedOut));
            Assert.That(store.Read(auth.HostKey), Is.Null);
            Assert.That(cache.Load(auth.HostKey), Is.Null);
            var e = Assert.Throws<EnluException>(() => Sync.Run(() => auth.SendAsync("GET", "/v1/apps")));
            Assert.That(e!.Code, Is.EqualTo("not_signed_in"));
        }

        [Test]
        public void RestoreWithoutStoredTokenStaysSignedOut()
        {
            var auth = new EnluEditorAuth(Base, new EnluMemoryCredentialStore(), new EnluMemoryTokenCache(), new HttpClient(new FakeOAuthServer()));
            Assert.That(Sync.Run(() => auth.TryRestoreAsync()), Is.False);
            Assert.That(auth.Status, Is.EqualTo(EnluAuthStatus.SignedOut));
        }

        [Test]
        public void TokenSetRejectsIncompleteResponses()
        {
            Assert.Throws<EnluException>(() => EnluTokenSet.FromJson(EnluJson.Parse("{\"access_token\":\"a\",\"token_type\":\"Bearer\"}"), DateTime.UtcNow));
            Assert.Throws<EnluException>(() => EnluTokenSet.FromJson(EnluJson.Parse("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"token_type\":\"mac\"}"), DateTime.UtcNow));
            var t = EnluTokenSet.FromJson(EnluJson.Parse(new FakeOAuthServer().TokenJson()), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.That(t.AccessExpiresAtUtc, Is.EqualTo(new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc)));
            Assert.That(t.ToString(), Does.Not.Contain("enlu_"));
        }

        [Test]
        public void TitleDataSaveKeepsInternalKeysInternal()
        {
            var bodies = new List<string>();
            var handler = new LambdaHandler(req =>
            {
                bodies.Add(req.Method + " " + req.RequestUri!.AbsolutePath + " " + (req.Content == null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
                return "{\"ok\":true}";
            });
            var store = new EnluMemoryCredentialStore();
            var cache = new EnluMemoryTokenCache();
            var host = EnluCredentialStores.HostKey(Base);
            cache.Save(host, "{\"a\":\"enlu_at_x\",\"e\":\"2999-01-01T00:00:00.000Z\",\"email\":\"d@e.com\"}");
            var auth = new EnluEditorAuth(Base, store, cache, new HttpClient(handler));
            var original = new Dictionary<string, object?> { ["motd"] = "hi", ["secretRate"] = 2, ["old"] = 1 };
            var edited = new Dictionary<string, object?> { ["motd"] = "hello", ["secretRate"] = 3 };
            Sync.Run(() => new EnluDashboardApi(auth).SaveTitleDataAsync("my-game", original, new List<string> { "secretRate" }, edited));
            Assert.That(bodies, Is.EqualTo(new List<string>
            {
                "PUT /v1/apps/my-game/title-data {\"data\":{\"motd\":\"hello\",\"old\":null},\"internal\":false}",
                "PUT /v1/apps/my-game/title-data {\"data\":{\"secretRate\":3},\"internal\":true}",
            }));
        }

        [Test]
        public void GameServerSpecHasTheFleetShape()
        {
            var spec = EnluDashboardApi.GameServerSpec("reg.example.com/g/arena:1", "game", 7777, "udp", 2, 20, 1, 1024);
            Assert.That(EnluJson.Serialize(spec), Is.EqualTo(
                "{\"kind\":\"game-server\",\"runtime\":\"docker\",\"image\":\"reg.example.com/g/arena:1\"," +
                "\"ports\":[{\"name\":\"game\",\"container\":7777,\"protocol\":\"udp\",\"public\":true}]," +
                "\"resources\":{\"cpu\":1,\"memoryMB\":1024},\"fleet\":{\"standby\":2,\"maxServers\":20}}"));
        }

        private sealed class LambdaHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, string> _f;
            public LambdaHandler(Func<HttpRequestMessage, string> f) => _f = f;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_f(request), Encoding.UTF8, "application/json") });
        }
    }
}
