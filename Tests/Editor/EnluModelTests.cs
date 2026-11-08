#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace Enlu.Tests
{
    public sealed class EnluModelTests
    {
        [Test]
        public void ReadsLoginResult()
        {
            var r = LoginResult.FromJson(EnluJson.Parse(
                "{\"sessionTicket\":\"enlu_ps_secret\",\"playerId\":\"pl_abc\",\"expiresAt\":\"2026-11-09T10:00:00.000Z\",\"newlyCreated\":true}"));
            Assert.That(r.SessionTicket, Is.EqualTo("enlu_ps_secret"));
            Assert.That(r.PlayerId, Is.EqualTo("pl_abc"));
            Assert.That(r.NewlyCreated, Is.True);
            Assert.That(r.ExpiresAt, Is.EqualTo(new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc)));
            Assert.That(r.ToString(), Does.Not.Contain("enlu_ps_secret"));
        }

        [Test]
        public void RejectsLoginWithoutTicket()
        {
            var e = Assert.Throws<EnluException>(() => LoginResult.FromJson(EnluJson.Parse("{\"playerId\":\"pl_x\"}")));
            Assert.That(e!.Code, Is.EqualTo("bad_response"));
        }

        [Test]
        public void ReadsProfileWithStatistics()
        {
            var p = PlayerProfile.FromJson(EnluJson.Parse(
                "{\"id\":\"pl_1\",\"customId\":\"device:abc\",\"email\":null,\"displayName\":\"Ollie\",\"banned\":false,\"banReason\":null," +
                "\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"lastLogin\":null,\"loginCount\":4," +
                "\"statistics\":{\"score\":{\"value\":1200,\"version\":2},\"time\":{\"value\":12.5,\"version\":0}}}"));
            Assert.That(p.Id, Is.EqualTo("pl_1"));
            Assert.That(p.CustomId, Is.EqualTo("device:abc"));
            Assert.That(p.Email, Is.Null);
            Assert.That(p.DisplayName, Is.EqualTo("Ollie"));
            Assert.That(p.LoginCount, Is.EqualTo(4));
            Assert.That(p.LastLogin, Is.Null);
            Assert.That(p.Statistics["score"].Value, Is.EqualTo(1200));
            Assert.That(p.Statistics["score"].Version, Is.EqualTo(2));
            Assert.That(p.Statistics["time"].Value, Is.EqualTo(12.5));
        }

        [Test]
        public void ReadsPlayerData()
        {
            var map = PlayerDataEntry.MapFromResponse(EnluJson.Parse(
                "{\"data\":{\"coins\":{\"value\":250,\"access\":\"readonly\",\"visibility\":\"private\",\"updatedAt\":\"2026-01-01T00:00:00.000Z\"}," +
                "\"loadout\":{\"value\":{\"weapon\":\"bow\",\"skins\":[\"red\"]},\"access\":\"client\",\"visibility\":\"public\",\"updatedAt\":\"2026-01-01T00:00:00.000Z\"}}}"));
            Assert.That(map.Count, Is.EqualTo(2));
            Assert.That(map["coins"].GetValue<int>(), Is.EqualTo(250));
            Assert.That(map["coins"].Access, Is.EqualTo("readonly"));
            Assert.That(map["loadout"].Visibility, Is.EqualTo("public"));
            var loadout = map["loadout"].GetValue<Dictionary<string, object?>>();
            Assert.That(loadout["weapon"], Is.EqualTo("bow"));
            Assert.That(map["loadout"].ValueJson, Is.EqualTo("{\"weapon\":\"bow\",\"skins\":[\"red\"]}"));
        }

        [Test]
        public void ReadsEmptyOrMissingDataAsEmpty()
        {
            Assert.That(PlayerDataEntry.MapFromResponse(EnluJson.Parse("{\"data\":{}}")), Is.Empty);
            Assert.That(PlayerDataEntry.MapFromResponse(null), Is.Empty);
        }

        [Test]
        public void ReadsLeaderboard()
        {
            var lb = Leaderboard.FromJson(EnluJson.Parse(
                "{\"statistic\":\"score\",\"version\":1,\"entries\":[" +
                "{\"position\":1,\"playerId\":\"pl_a\",\"displayName\":\"Ann\",\"value\":900}," +
                "{\"position\":2,\"playerId\":\"pl_b\",\"displayName\":null,\"value\":850.5}]}"));
            Assert.That(lb.Statistic, Is.EqualTo("score"));
            Assert.That(lb.Version, Is.EqualTo(1));
            Assert.That(lb.Entries.Count, Is.EqualTo(2));
            Assert.That(lb.Entries[0].DisplayName, Is.EqualTo("Ann"));
            Assert.That(lb.Entries[1].DisplayName, Is.Null);
            Assert.That(lb.Entries[1].Value, Is.EqualTo(850.5));
            Assert.That(lb.Entries[1].ToString(), Is.EqualTo("2. pl_b 850.5"));
        }

        [Test]
        public void ReadsTypedFunctionResults()
        {
            var ok = FunctionResult<int>.FromJson(EnluJson.Parse("{\"ok\":true,\"result\":42,\"ms\":3.2}"));
            Assert.That(ok.Ok, Is.True);
            Assert.That(ok.Result, Is.EqualTo(42));
            Assert.That(ok.Ms, Is.EqualTo(3.2));
            Assert.That(ok.EnsureOk(), Is.EqualTo(42));

            var failed = FunctionResult<int>.FromJson(EnluJson.Parse("{\"ok\":false,\"result\":null,\"error\":\"Not enough coins\",\"ms\":1}"));
            Assert.That(failed.Ok, Is.False);
            Assert.That(failed.Error, Is.EqualTo("Not enough coins"));
            var e = Assert.Throws<EnluException>(() => failed.EnsureOk());
            Assert.That(e!.Code, Is.EqualTo("function_error"));
            Assert.That(e.Message, Is.EqualTo("Not enough coins"));

            var raw = FunctionResult<object>.FromJson(EnluJson.Parse(
                "{\"ok\":true,\"result\":{\"granted\":[\"gem\"]},\"logs\":[{\"level\":\"info\",\"message\":\"hi\"}],\"ms\":2,\"backend\":\"inline\"}"));
            Assert.That(EnluJson.GetStringList(EnluJson.AsObject(raw.Result), "granted"), Is.EqualTo(new List<string> { "gem" }));
            Assert.That(raw.Logs.Count, Is.EqualTo(1));
            Assert.That(raw.Logs[0].Message, Is.EqualTo("hi"));
            Assert.That(raw.Backend, Is.EqualTo("inline"));
        }

        [Test]
        public void BadFunctionResultTypeIsReported()
        {
            var e = Assert.Throws<EnluException>(() => FunctionResult<int>.FromJson(EnluJson.Parse("{\"ok\":true,\"result\":\"text\",\"ms\":1}")));
            Assert.That(e!.Code, Is.EqualTo("bad_response"));
        }

        [Test]
        public void ReadsAllocations()
        {
            var a = SessionAllocation.FromJson(EnluJson.Parse(
                "{\"sessionId\":\"ss_1\",\"instanceId\":\"in_1\",\"workload\":\"arena\",\"region\":\"eu-west\",\"node\":\"n1\"," +
                "\"address\":\"203.0.113.7\",\"ports\":[{\"name\":\"game\",\"port\":20017,\"protocol\":\"udp\"},{\"name\":\"query\",\"port\":20018,\"protocol\":\"tcp\"}]," +
                "\"allocatedAt\":\"2026-11-08T12:00:00.000Z\"}"));
            Assert.That(a.SessionId, Is.EqualTo("ss_1"));
            Assert.That(a.GetPort("GAME")!.Protocol, Is.EqualTo("udp"));
            Assert.That(a.GetPortNumber(), Is.EqualTo(20017));
            Assert.That(a.GetPortNumber("query"), Is.EqualTo(20018));
            Assert.That(a.GetPortNumber("missing"), Is.EqualTo(0));
            Assert.That(a.GetEndpoint(), Is.EqualTo("203.0.113.7:20017"));
            Assert.That(a.GetEndpoint("query"), Is.EqualTo("203.0.113.7:20018"));

            var v6 = SessionAllocation.FromJson(EnluJson.Parse("{\"sessionId\":\"s\",\"address\":\"2001:db8::1\",\"ports\":[{\"name\":\"game\",\"port\":7777,\"protocol\":\"udp\"}]}"));
            Assert.That(v6.GetEndpoint(), Is.EqualTo("[2001:db8::1]:7777"));

            var noAddress = SessionAllocation.FromJson(EnluJson.Parse("{\"sessionId\":\"s\",\"address\":null,\"ports\":[]}"));
            Assert.That(noAddress.GetEndpoint(), Is.Null);
        }

        [Test]
        public void SerializesAllocationRequests()
        {
            var req = new AllocationRequest { Workload = "arena", SessionId = "match-1" };
            req.Regions.Add("eu-west");
            req.Players.Add("pl_a");
            req.Metadata["map"] = "desert";
            Assert.That(EnluJson.Serialize(req.ToJsonObject()), Is.EqualTo(
                "{\"workload\":\"arena\",\"sessionId\":\"match-1\",\"regions\":[\"eu-west\"],\"players\":[\"pl_a\"],\"metadata\":{\"map\":\"desert\"}}"));
            Assert.That(EnluJson.Serialize(new AllocationRequest { Workload = "arena" }.ToJsonObject()), Is.EqualTo("{\"workload\":\"arena\"}"));
            Assert.Throws<ArgumentException>(() => new AllocationRequest().ToJsonObject());
        }

        [Test]
        public void ReadsExports()
        {
            var x = PlayerExport.FromJson(EnluJson.Parse(
                "{\"exportedAt\":\"2026-11-08T00:00:00.000Z\",\"appId\":\"app_1\",\"profile\":{\"id\":\"pl_1\",\"loginCount\":1}," +
                "\"data\":{\"k\":{\"value\":1,\"access\":\"internal\",\"visibility\":\"private\",\"updatedAt\":\"2026-11-08T00:00:00.000Z\"}}," +
                "\"statistics\":[{\"name\":\"score\",\"version\":0,\"value\":5,\"updatedAt\":\"2026-11-08T00:00:00.000Z\"}]," +
                "\"events\":[{\"name\":\"login\",\"body\":{},\"source\":\"client\",\"ts\":\"2026-11-08T00:00:00.000Z\"}]," +
                "\"sessions\":[]}"));
            Assert.That(x.AppId, Is.EqualTo("app_1"));
            Assert.That(x.Profile.Id, Is.EqualTo("pl_1"));
            Assert.That(x.Data["k"].Access, Is.EqualTo("internal"));
            Assert.That(x.Statistics[0].Name, Is.EqualTo("score"));
            Assert.That(x.Events[0].Source, Is.EqualTo("client"));
            Assert.That(x.Raw.ContainsKey("sessions"), Is.True);
            Assert.That(EnluJson.TryParse(x.ToJson(), out _), Is.True);
        }

        [Test]
        public void EventsValidateNames()
        {
            Assert.Throws<ArgumentException>(() => new EnluEvent("1bad"));
            Assert.That(EnluJson.Serialize(new EnluEvent("level_up").ToJsonObject()), Is.EqualTo("{\"name\":\"level_up\",\"body\":{}}"));
        }

        [Test]
        public void ReadsErrorResponses()
        {
            var e = EnluException.FromResponse(403, "{\"code\":\"banned\",\"message\":\"This account is banned.\",\"details\":{\"until\":null}}", null);
            Assert.That(e.Code, Is.EqualTo("banned"));
            Assert.That(e.Message, Is.EqualTo("This account is banned."));
            Assert.That(e.HttpStatus, Is.EqualTo(403));
            Assert.That(e.Details, Is.InstanceOf<Dictionary<string, object?>>());

            var html = EnluException.FromResponse(502, "<html>Bad gateway</html>", null);
            Assert.That(html.Code, Is.EqualTo("server_error"));
            Assert.That(html.Message, Does.Contain("502"));

            var limited = EnluException.FromResponse(429, "{\"code\":\"rate_limited\",\"message\":\"Too many requests.\"}", 1);
            Assert.That(limited.RetryAfter, Is.EqualTo(1));
            Assert.That(limited.IsTransient, Is.True);
            Assert.That(EnluException.FromResponse(401, "", null).IsUnauthorized, Is.True);
        }

        [Test]
        public void ParsesRetryAfter()
        {
            Assert.That(EnluException.ParseRetryAfter("3"), Is.EqualTo(3));
            Assert.That(EnluException.ParseRetryAfter(" 1.5 "), Is.EqualTo(1.5));
            Assert.That(EnluException.ParseRetryAfter(null), Is.Null);
            Assert.That(EnluException.ParseRetryAfter("soon"), Is.Null);
            var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
            Assert.That(EnluException.ParseRetryAfter("Sun, 08 Nov 2026 12:00:10 GMT", now), Is.EqualTo(10).Within(0.001));
        }

        [Test]
        public void RetryPolicyFollowsTheRules()
        {
            var p = new EnluRetryPolicy(new Random(1)) { MaxRetries = 2 };
            Assert.That(p.ShouldRetry(EnluRetryMode.RejectedOnly, 429, 0), Is.True);
            Assert.That(p.ShouldRetry(EnluRetryMode.RejectedOnly, 503, 0), Is.False);
            Assert.That(p.ShouldRetry(EnluRetryMode.RejectedOnly, 0, 0), Is.False);
            Assert.That(p.ShouldRetry(EnluRetryMode.RejectedOrUnavailable, 503, 0), Is.True);
            Assert.That(p.ShouldRetry(EnluRetryMode.RejectedOrUnavailable, 0, 0), Is.False);
            Assert.That(p.ShouldRetry(EnluRetryMode.Idempotent, 0, 1), Is.True);
            Assert.That(p.ShouldRetry(EnluRetryMode.Idempotent, 0, 2), Is.False);
            Assert.That(p.ShouldRetry(EnluRetryMode.Idempotent, 500, 0), Is.False);
            Assert.That(p.ShouldRetry(EnluRetryMode.Idempotent, 404, 0), Is.False);

            for (var attempt = 0; attempt < 10; attempt++)
            {
                var d = p.GetDelaySeconds(attempt, null);
                Assert.That(d, Is.GreaterThan(0).And.LessThanOrEqualTo(p.MaxDelaySeconds));
            }
            Assert.That(p.GetDelaySeconds(0, 3), Is.EqualTo(3));
            Assert.That(p.GetDelaySeconds(0, 999), Is.EqualTo(p.MaxRetryAfterSeconds));
        }

        [Test]
        public void SerializesHeartbeats()
        {
            var hb = new GsdkHeartbeatRequest { State = GameServerState.StandingBy, Healthy = true };
            hb.Players.Add("pl_a");
            Assert.That(hb.ToJson(), Is.EqualTo("{\"state\":\"StandingBy\",\"players\":[\"pl_a\"],\"health\":\"Healthy\"}"));
            Assert.That(new GsdkHeartbeatRequest { State = GameServerState.Terminating, Healthy = false }.ToJson(),
                Is.EqualTo("{\"state\":\"Terminating\",\"players\":[],\"health\":\"Unhealthy\"}"));
        }

        [Test]
        public void ParsesHeartbeatResponses()
        {
            var r = GsdkHeartbeatResponse.Parse("{\"operation\":\"Active\",\"sessionId\":\"ss_9\",\"sessionMetadata\":{\"map\":\"desert\",\"round\":3},\"initialPlayers\":[\"pl_a\",\"pl_b\"]}");
            Assert.That(r.Operation, Is.EqualTo(GsdkOperation.Active));
            Assert.That(r.SessionId, Is.EqualTo("ss_9"));
            Assert.That(r.SessionMetadata["map"], Is.EqualTo("desert"));
            Assert.That(r.SessionMetadata["round"], Is.EqualTo("3"));
            Assert.That(r.InitialPlayers, Is.EqualTo(new List<string> { "pl_a", "pl_b" }));

            Assert.That(GsdkHeartbeatResponse.Parse("{\"operation\":\"Continue\",\"sessionId\":null,\"sessionMetadata\":{},\"initialPlayers\":[]}").Operation, Is.EqualTo(GsdkOperation.Continue));
            Assert.That(GsdkHeartbeatResponse.Parse("{\"operation\":\"Terminate\"}").Operation, Is.EqualTo(GsdkOperation.Terminate));
            Assert.That(GsdkHeartbeatResponse.Parse("{\"operation\":\"SomethingNew\"}").Operation, Is.EqualTo(GsdkOperation.Continue));
            Assert.That(GsdkHeartbeatResponse.Parse("").Operation, Is.EqualTo(GsdkOperation.Continue));
        }

        [Test]
        public void ReadsGsdkEnvironment()
        {
            var env = new Hashtable
            {
                ["ENLU_GSDK_URL"] = "http://172.17.0.1:56001/",
                ["ENLU_GSDK_TOKEN"] = "tok_secret",
                ["ENLU_INSTANCE_ID"] = "in_1",
                ["ENLU_APP_ID"] = "app_1",
                ["ENLU_WORKLOAD"] = "arena",
                ["ENLU_REGION"] = "eu-west",
                ["ENLU_NODE"] = "node-1",
                ["ENLU_PUBLIC_ADDRESS"] = "203.0.113.7",
                ["ENLU_PORT_GAME"] = "7777",
                ["ENLU_HOST_PORT_GAME"] = "20017",
                ["ENLU_PORT_QUERY_PORT"] = "27015",
                ["ENLU_PORT_BROKEN"] = "nope",
                ["PATH"] = "/usr/bin",
            };
            var c = GsdkConfig.FromEnvironment(env);
            Assert.That(c.IsLocal, Is.False);
            Assert.That(c.GsdkUrl, Is.EqualTo("http://172.17.0.1:56001"));
            Assert.That(c.InstanceId, Is.EqualTo("in_1"));
            Assert.That(c.Region, Is.EqualTo("eu-west"));
            Assert.That(c.PublicAddress, Is.EqualTo("203.0.113.7"));
            Assert.That(c.GetContainerPort("game"), Is.EqualTo(7777));
            Assert.That(c.GetPublicPort("game"), Is.EqualTo(20017));
            Assert.That(c.GetContainerPort("query-port"), Is.EqualTo(27015));
            Assert.That(c.GetPublicPort("query-port"), Is.EqualTo(27015), "falls back to the container port");
            Assert.That(c.GetPublicPort("missing", 1234), Is.EqualTo(1234));
            Assert.That(c.GetContainerPort("broken", -1), Is.EqualTo(-1));
            Assert.That(c.ToString(), Does.Not.Contain("tok_secret"));

            Assert.That(GsdkConfig.FromEnvironment(new Hashtable()).IsLocal, Is.True);
            Assert.That(GsdkConfig.FromEnvironment(new Hashtable { ["ENLU_GSDK_URL"] = " " }).IsLocal, Is.True);
        }
    }
}
