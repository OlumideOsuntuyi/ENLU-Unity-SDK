#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Enlu.Tests
{
    /// <summary>Answers requests synchronously from a script, recording what was sent.</summary>
    internal sealed class FakeTransport : IEnluTransport
    {
        public readonly List<EnluHttpRequest> Requests = new List<EnluHttpRequest>();
        public readonly Queue<EnluHttpResponse> Responses = new Queue<EnluHttpResponse>();
        public Func<EnluHttpRequest, EnluHttpResponse>? Handler;

        public void Enqueue(int status, string body, string? retryAfter = null)
        {
            var r = new EnluHttpResponse { Status = status, Body = body };
            if (retryAfter != null) r.Headers["retry-after"] = retryAfter;
            Responses.Enqueue(r);
        }

        public void EnqueueNetworkError() => Responses.Enqueue(new EnluHttpResponse { Status = 0, NetworkError = "Cannot resolve destination host" });

        public Task<EnluHttpResponse> SendAsync(EnluHttpRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Handler != null) return Task.FromResult(Handler(request));
            if (Responses.Count == 0) throw new InvalidOperationException("No scripted response for " + request.Method + " " + request.Url);
            return Task.FromResult(Responses.Dequeue());
        }

        public EnluHttpRequest Last => Requests[Requests.Count - 1];
    }

    /// <summary>Records delays and completes immediately.</summary>
    internal sealed class FakeScheduler : IEnluScheduler
    {
        public readonly List<double> Delays = new List<double>();

        public Task Delay(double seconds, CancellationToken cancellationToken)
        {
            Delays.Add(seconds);
            return Task.CompletedTask;
        }
    }

    public sealed class EnluClientTests
    {
        private const string Base = "https://api.example.com";
        private const string LoginJson = "{\"sessionTicket\":\"enlu_ps_TICKET\",\"playerId\":\"pl_1\",\"expiresAt\":\"2999-01-01T00:00:00.000Z\",\"newlyCreated\":false}";

        private FakeTransport _transport = null!;
        private FakeScheduler _scheduler = null!;
        private EnluMemoryStore _store = null!;

        [SetUp]
        public void SetUp()
        {
            _transport = new FakeTransport();
            _scheduler = new FakeScheduler();
            _store = new EnluMemoryStore();
        }

        private EnluClient NewClient(IEnluKeyValueStore? store = null) =>
            new EnluClient(new EnluClientOptions { BaseUrl = Base, App = "game", MaxRetries = 3 }, _transport, _scheduler, store);

        private EnluClient LoggedInClient(IEnluKeyValueStore? store = null)
        {
            var c = NewClient(store);
            _transport.Enqueue(200, LoginJson);
            c.LoginWithCustomIdAsync("player_1").GetAwaiter().GetResult();
            _transport.Requests.Clear();
            return c;
        }

        private static Dictionary<string, object?> Body(EnluHttpRequest r) => EnluJson.ParseObject(r.Body!);

        [Test]
        public void CustomIdLoginSendsTheRightRequestAndKeepsTheTicket()
        {
            var c = NewClient();
            LoginResult? fired = null;
            c.LoggedIn += r => fired = r;
            _transport.Enqueue(200, LoginJson);

            var res = c.LoginWithCustomIdAsync("player_1", createAccount: false).GetAwaiter().GetResult();

            var req = _transport.Last;
            Assert.That(req.Method, Is.EqualTo("POST"));
            Assert.That(req.Url, Is.EqualTo(Base + "/client/v1/game/login/custom"));
            Assert.That(req.Headers["Content-Type"], Is.EqualTo("application/json"));
            Assert.That(req.Headers.ContainsKey("X-Enlu-Session"), Is.False);
            Assert.That(Body(req)["customId"], Is.EqualTo("player_1"));
            Assert.That(Body(req)["createAccount"], Is.EqualTo(false));
            Assert.That(res.PlayerId, Is.EqualTo("pl_1"));
            Assert.That(c.IsLoggedIn, Is.True);
            Assert.That(c.PlayerId, Is.EqualTo("pl_1"));
            Assert.That(fired, Is.SameAs(res));
            Assert.That(c.ToString(), Does.Not.Contain("TICKET"));
        }

        [Test]
        public void DeviceLoginUsesThePrefixedCustomId()
        {
            var c = NewClient();
            _transport.Enqueue(200, LoginJson);
            c.LoginWithDeviceIdAsync("ABC-123").GetAwaiter().GetResult();
            Assert.That(Body(_transport.Last)["customId"], Is.EqualTo("device:ABC-123"));
        }

        [Test]
        public void EmailRegistrationAndLogin()
        {
            var c = NewClient();
            _transport.Enqueue(201, LoginJson.Replace("false}", "true}"));
            var reg = c.RegisterWithEmailAsync("a@example.com", "correct horse", "Ann").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/register"));
            Assert.That(Body(_transport.Last)["displayName"], Is.EqualTo("Ann"));
            Assert.That(reg.NewlyCreated, Is.True);

            _transport.Enqueue(200, LoginJson);
            c.LoginWithEmailAsync("a@example.com", "correct horse").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/login/email"));
            Assert.That(Body(_transport.Last)["password"], Is.EqualTo("correct horse"));

            Assert.Throws<ArgumentException>(() => c.RegisterWithEmailAsync("a@example.com", "short").GetAwaiter().GetResult());
        }

        [Test]
        public void PlayerCallsNeedALogin()
        {
            var c = NewClient();
            var e = Assert.Throws<EnluException>(() => c.GetProfileAsync().GetAwaiter().GetResult());
            Assert.That(e!.Code, Is.EqualTo("not_logged_in"));
            Assert.That(_transport.Requests, Is.Empty);
        }

        [Test]
        public void SendsTheTicketHeader()
        {
            var c = LoggedInClient();
            _transport.Enqueue(200, "{\"id\":\"pl_1\",\"displayName\":\"Ann\",\"loginCount\":2,\"statistics\":{}}");
            var p = c.GetProfileAsync().GetAwaiter().GetResult();
            Assert.That(_transport.Last.Method, Is.EqualTo("GET"));
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/me"));
            Assert.That(_transport.Last.Headers["X-Enlu-Session"], Is.EqualTo("enlu_ps_TICKET"));
            Assert.That(_transport.Last.Body, Is.Null);
            Assert.That(p.DisplayName, Is.EqualTo("Ann"));
        }

        [Test]
        public void ProfileUpdateExportAndDelete()
        {
            var c = LoggedInClient();
            var loggedOut = 0;
            c.LoggedOut += () => loggedOut++;

            _transport.Enqueue(200, "{\"id\":\"pl_1\",\"displayName\":\"Neo\"}");
            Assert.That(c.UpdateDisplayNameAsync("Neo").GetAwaiter().GetResult().DisplayName, Is.EqualTo("Neo"));
            Assert.That(_transport.Last.Method, Is.EqualTo("PATCH"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"displayName\":\"Neo\"}"));

            _transport.Enqueue(200, "{\"appId\":\"app_1\",\"profile\":{\"id\":\"pl_1\"},\"data\":{},\"statistics\":[],\"events\":[],\"sessions\":[]}");
            Assert.That(c.ExportMyDataAsync().GetAwaiter().GetResult().Profile.Id, Is.EqualTo("pl_1"));
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/me/export"));

            _transport.Enqueue(200, "{\"deleted\":\"pl_1\"}");
            Assert.That(c.DeleteMyAccountAsync().GetAwaiter().GetResult(), Is.EqualTo("pl_1"));
            Assert.That(_transport.Last.Method, Is.EqualTo("DELETE"));
            Assert.That(c.IsLoggedIn, Is.False);
            Assert.That(loggedOut, Is.EqualTo(1));
        }

        [Test]
        public void ReadsAndWritesData()
        {
            var c = LoggedInClient();
            _transport.Enqueue(200, "{\"data\":{\"coins\":{\"value\":5,\"access\":\"client\",\"visibility\":\"private\",\"updatedAt\":\"2026-01-01T00:00:00.000Z\"}}}");
            var data = c.GetDataAsync("coins", "level").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/data?keys=coins%2Clevel"));
            Assert.That(data["coins"].GetValue<int>(), Is.EqualTo(5));

            _transport.Enqueue(200, "{\"updated\":2}");
            var n = c.SetDataAsync(new Dictionary<string, object?> { ["coins"] = 6, ["old"] = null }, DataVisibility.Public).GetAwaiter().GetResult();
            Assert.That(n, Is.EqualTo(2));
            Assert.That(_transport.Last.Method, Is.EqualTo("PUT"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"data\":{\"coins\":6,\"old\":null},\"visibility\":\"public\"}"));

            Assert.Throws<ArgumentException>(() => c.SetDataAsync("bad key", 1).GetAwaiter().GetResult());
        }

        [Test]
        public void ReadsOtherPlayersAndTitleData()
        {
            var c = LoggedInClient();
            _transport.Enqueue(200, "{\"data\":{}}");
            c.GetPlayerPublicDataAsync("pl_2").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/players/pl_2/data"));

            _transport.Enqueue(200, "{\"data\":{\"motd\":\"Welcome\",\"rates\":{\"xp\":2}}}");
            var title = c.GetTitleDataAsync().GetAwaiter().GetResult();
            Assert.That(title["motd"], Is.EqualTo("Welcome"));
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/title-data"));
        }

        [Test]
        public void StatisticsAndLeaderboards()
        {
            var c = LoggedInClient();
            _transport.Enqueue(200, "{\"statistics\":{\"score\":1500}}");
            var stored = c.UpdateStatisticAsync("score", 1200).GetAwaiter().GetResult();
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"statistics\":{\"score\":1200}}"));
            Assert.That(stored["score"], Is.EqualTo(1500), "the server's aggregated value is returned");

            _transport.Enqueue(200, "{\"statistic\":\"score\",\"version\":0,\"entries\":[{\"position\":4,\"playerId\":\"pl_1\",\"displayName\":null,\"value\":1500}]}");
            var lb = c.GetLeaderboardAsync("score", 500, aroundMe: true).GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/leaderboards/score?top=100&around=me"));
            Assert.That(lb.Entries[0].Position, Is.EqualTo(4));

            Assert.Throws<ArgumentException>(() => c.UpdateStatisticAsync("score", double.NaN).GetAwaiter().GetResult());
        }

        [Test]
        public void FunctionsAndSessions()
        {
            var c = LoggedInClient();
            _transport.Enqueue(200, "{\"ok\":true,\"result\":{\"coins\":10},\"ms\":4}");
            var res = c.ExecuteFunctionAsync<Dictionary<string, object?>>("grantReward", new Dictionary<string, object?> { ["chest"] = "gold" }).GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/functions/grantReward"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"args\":{\"chest\":\"gold\"}}"));
            Assert.That(res.Result["coins"], Is.EqualTo(10L));

            _transport.Enqueue(200, "{\"ok\":true,\"result\":null,\"ms\":1}");
            c.ExecuteFunctionAsync("ping").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"args\":{}}"));
            Assert.Throws<ArgumentException>(() => c.ExecuteFunctionAsync("bad-name").GetAwaiter().GetResult());

            _transport.Enqueue(201, "{\"sessionId\":\"ss_1\",\"instanceId\":\"in_1\",\"workload\":\"arena\",\"region\":\"eu\",\"node\":\"n\",\"address\":\"1.2.3.4\",\"ports\":[{\"name\":\"game\",\"port\":20001,\"protocol\":\"udp\"}],\"allocatedAt\":\"\"}");
            var s = c.RequestSessionAsync("arena", new[] { "eu" }, new Dictionary<string, string> { ["map"] = "dunes" }).GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/sessions"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"workload\":\"arena\",\"regions\":[\"eu\"],\"metadata\":{\"map\":\"dunes\"}}"));
            Assert.That(s.GetEndpoint("game"), Is.EqualTo("1.2.3.4:20001"));
        }

        [Test]
        public void RetriesRateLimitsUsingRetryAfter()
        {
            var c = LoggedInClient();
            _transport.Enqueue(429, "{\"code\":\"rate_limited\",\"message\":\"Too many requests.\"}", "2");
            _transport.Enqueue(200, "{\"written\":1}");
            c.WriteEventAsync("level_up").GetAwaiter().GetResult();
            Assert.That(_transport.Requests.Count, Is.EqualTo(2));
            Assert.That(_scheduler.Delays, Is.EqualTo(new List<double> { 2 }));
        }

        [Test]
        public void DoesNotRetryNonIdempotentCallsOnNetworkErrorsOr503()
        {
            var c = LoggedInClient();
            _transport.EnqueueNetworkError();
            var e = Assert.Throws<EnluException>(() => c.UpdateStatisticAsync("score", 1).GetAwaiter().GetResult());
            Assert.That(e!.Code, Is.EqualTo("network_error"));
            Assert.That(e.IsNetworkError, Is.True);
            Assert.That(_transport.Requests.Count, Is.EqualTo(1));

            _transport.Requests.Clear();
            _transport.Enqueue(503, "{\"code\":\"unavailable\",\"message\":\"Busy.\"}");
            Assert.Throws<EnluException>(() => c.ExecuteFunctionAsync("x").GetAwaiter().GetResult());
            Assert.That(_transport.Requests.Count, Is.EqualTo(1));
        }

        [Test]
        public void RetriesIdempotentCallsOnNetworkErrors()
        {
            var c = LoggedInClient();
            _transport.EnqueueNetworkError();
            _transport.Enqueue(503, "{\"code\":\"unavailable\",\"message\":\"Busy.\"}", "1");
            _transport.Enqueue(200, "{\"data\":{}}");
            c.GetDataAsync().GetAwaiter().GetResult();
            Assert.That(_transport.Requests.Count, Is.EqualTo(3));
            Assert.That(_scheduler.Delays.Count, Is.EqualTo(2));
        }

        [Test]
        public void GivesUpAfterMaxRetries()
        {
            var c = LoggedInClient();
            _transport.Handler = _ => new EnluHttpResponse { Status = 503, Body = "{\"code\":\"unavailable\",\"message\":\"No standby server right now.\"}" };
            var e = Assert.Throws<EnluException>(() => c.RequestSessionAsync("arena").GetAwaiter().GetResult());
            Assert.That(e!.Code, Is.EqualTo("unavailable"));
            Assert.That(e.HttpStatus, Is.EqualTo(503));
            Assert.That(_transport.Requests.Count, Is.EqualTo(4), "1 attempt + 3 retries");
        }

        [Test]
        public void UnauthorizedClearsTheSession()
        {
            var store = new EnluMemoryStore();
            var c = LoggedInClient(store);
            var expired = 0;
            c.SessionExpired += () => expired++;
            _transport.Enqueue(401, "{\"code\":\"unauthorized\",\"message\":\"The session has expired. Log in again.\"}");
            var e = Assert.Throws<EnluException>(() => c.GetProfileAsync().GetAwaiter().GetResult());
            Assert.That(e!.IsUnauthorized, Is.True);
            Assert.That(c.IsLoggedIn, Is.False);
            Assert.That(expired, Is.EqualTo(1));
            Assert.That(store.Get("enlu.game.session"), Is.Null);
        }

        [Test]
        public void PersistsAndRestoresTheSession()
        {
            var store = new EnluMemoryStore();
            LoggedInClient(store);
            Assert.That(store.Get("enlu.game.session"), Does.Contain("enlu_ps_TICKET"));

            var restored = NewClient(store);
            Assert.That(restored.IsLoggedIn, Is.True);
            Assert.That(restored.PlayerId, Is.EqualTo("pl_1"));

            store.Set("enlu.game.session", "{\"t\":\"x\",\"p\":\"pl_1\",\"e\":\"2000-01-01T00:00:00.000Z\"}");
            Assert.That(NewClient(store).IsLoggedIn, Is.False, "expired tickets are not restored");
            store.Set("enlu.game.session", "not json");
            Assert.That(NewClient(store).IsLoggedIn, Is.False);
        }

        [Test]
        public void LogoutClearsLocallyEvenWhenTheServerIsDown()
        {
            var store = new EnluMemoryStore();
            var c = LoggedInClient(store);
            _transport.EnqueueNetworkError();
            c.LogoutAsync().GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/logout"));
            Assert.That(_transport.Last.Headers["X-Enlu-Session"], Is.EqualTo("enlu_ps_TICKET"));
            Assert.That(c.IsLoggedIn, Is.False);
            Assert.That(store.Get("enlu.game.session"), Is.Null);
        }

        [Test]
        public void QueuedEventsAreSentInBatchesOf50()
        {
            var c = LoggedInClient();
            var batchSizes = new List<int>();
            _transport.Handler = r =>
            {
                var n = EnluJson.GetArray(Body(r), "events")!.Count;
                batchSizes.Add(n);
                return new EnluHttpResponse { Status = 200, Body = "{\"written\":" + n + "}" };
            };
            for (var i = 0; i < 49; i++) c.QueueEvent("tick", new Dictionary<string, object?> { ["i"] = i });
            Assert.That(_transport.Requests, Is.Empty, "below a full batch nothing is sent");
            for (var i = 0; i < 71; i++) c.QueueEvent("tick");
            // The 50th event triggers a background flush, which sends everything queued so far.
            var written = c.FlushAsync().GetAwaiter().GetResult();
            Assert.That(c.PendingEventCount, Is.EqualTo(0));
            var total = 0;
            foreach (var b in batchSizes)
            {
                Assert.That(b, Is.LessThanOrEqualTo(50));
                total += b;
            }
            Assert.That(total, Is.EqualTo(120));
            Assert.That(written, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void FailedFlushKeepsTheEvents()
        {
            var c = LoggedInClient();
            c.QueueEvent("a");
            c.QueueEvent("b");
            _transport.Enqueue(500, "{\"code\":\"internal\",\"message\":\"Something went wrong on the server.\"}");
            Assert.Throws<EnluException>(() => c.FlushAsync().GetAwaiter().GetResult());
            Assert.That(c.PendingEventCount, Is.EqualTo(2));

            _transport.Enqueue(200, "{\"written\":2}");
            Assert.That(c.FlushAsync().GetAwaiter().GetResult(), Is.EqualTo(2));
            Assert.That(Body(_transport.Last)["events"], Is.InstanceOf<List<object?>>());
            Assert.That(c.PendingEventCount, Is.EqualTo(0));
        }

        [Test]
        public void BadJsonFromTheServerIsReported()
        {
            var c = NewClient();
            _transport.Enqueue(200, "<html>proxy login</html>");
            var e = Assert.Throws<EnluException>(() => c.LoginWithCustomIdAsync("p").GetAwaiter().GetResult());
            Assert.That(e!.Code, Is.EqualTo("bad_response"));
        }

        [Test]
        public void ServerApiSendsTheKeyAndServerBodies()
        {
            var api = new EnluServerApi(new EnluClientOptions { BaseUrl = Base, App = "app_1" }, "enlu_key_SECRET", _transport, _scheduler);
            Assert.That(api.ToString(), Does.Not.Contain("SECRET"));

            _transport.Enqueue(201, "{\"sessionId\":\"match-7\",\"instanceId\":\"in_1\",\"workload\":\"arena\",\"region\":\"eu\",\"node\":\"n\",\"address\":\"1.2.3.4\",\"ports\":[]}");
            api.AllocateAsync("arena", "match-7", new[] { "eu" }, new[] { "pl_a", "pl_b" }).GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/v1/apps/app_1/allocations"));
            Assert.That(_transport.Last.Headers["Authorization"], Is.EqualTo("Bearer enlu_key_SECRET"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"workload\":\"arena\",\"sessionId\":\"match-7\",\"regions\":[\"eu\"],\"players\":[\"pl_a\",\"pl_b\"]}"));

            _transport.Enqueue(200, "{\"updated\":1}");
            api.SetPlayerDataAsync("pl_a", new Dictionary<string, object?> { ["coins"] = 100 }).GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/v1/apps/app_1/players/pl_a/data"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"data\":{\"coins\":100},\"access\":\"readonly\",\"visibility\":\"private\"}"));

            _transport.Enqueue(200, "{\"statistics\":{\"wins\":3}}");
            api.UpdatePlayerStatisticsAsync("pl_a", new Dictionary<string, double> { ["wins"] = 1 }).GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/v1/apps/app_1/players/pl_a/statistics"));

            _transport.Enqueue(200, "{\"ok\":true}");
            api.WriteEventAsync("match_end", new Dictionary<string, object?> { ["winner"] = "pl_a" }, "pl_a").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"name\":\"match_end\",\"body\":{\"winner\":\"pl_a\"},\"playerId\":\"pl_a\"}"));

            _transport.Enqueue(200, "{\"ok\":true,\"result\":7,\"logs\":[],\"ms\":1,\"backend\":\"inline\"}");
            var fn = api.ExecuteFunctionAsync<int>("settle", null, "pl_a").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"args\":{},\"playerId\":\"pl_a\"}"));
            Assert.That(fn.Result, Is.EqualTo(7));

            _transport.Enqueue(202, "{\"ending\":\"match-7\"}");
            api.EndSessionAsync("match-7").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Method, Is.EqualTo("DELETE"));
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/v1/apps/app_1/allocations/match-7"));
        }

        [Test]
        public void ServerApiRejectsMissingOrMalformedKeys()
        {
            var opts = new EnluClientOptions { BaseUrl = Base, App = "app_1" };
            Assert.Throws<ArgumentException>(() => new EnluServerApi(opts, "enlu_ps_player_ticket", _transport, _scheduler));
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnluServerApi.ApiKeyEnv)))
                Assert.Throws<InvalidOperationException>(() => new EnluServerApi(opts, null, _transport, _scheduler));
        }
    }
}
