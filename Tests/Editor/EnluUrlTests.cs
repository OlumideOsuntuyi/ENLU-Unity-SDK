#nullable enable
using System;
using NUnit.Framework;

namespace Enlu.Tests
{
    public sealed class EnluUrlTests
    {
        private const string Base = "https://api.example.com";

        [TestCase("https://api.example.com", "https://api.example.com")]
        [TestCase("https://api.example.com/", "https://api.example.com")]
        [TestCase("  https://api.example.com//  ", "https://api.example.com")]
        [TestCase("http://localhost:7700", "http://localhost:7700")]
        [TestCase("https://example.com/enlu", "https://example.com/enlu")]
        public void NormalizesBaseUrls(string input, string expected)
        {
            Assert.That(EnluUrl.NormalizeBaseUrl(input), Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("api.example.com")]
        [TestCase("ftp://api.example.com")]
        [TestCase("https://")]
        public void RejectsBadBaseUrls(string input)
        {
            Assert.Throws<ArgumentException>(() => EnluUrl.NormalizeBaseUrl(input));
        }

        [Test]
        public void EscapesPathSegments()
        {
            Assert.That(EnluUrl.EscapeSegment("a/b?c#d e"), Is.EqualTo("a%2Fb%3Fc%23d%20e"));
            Assert.Throws<ArgumentException>(() => EnluUrl.EscapeSegment(".."));
            Assert.Throws<ArgumentException>(() => EnluUrl.EscapeSegment(""));
        }

        [Test]
        public void BuildsClientRoutes()
        {
            var r = new EnluClientRoutes(Base + "/", " my-game ");
            Assert.That(r.LoginCustom(), Is.EqualTo(Base + "/client/v1/my-game/login/custom"));
            Assert.That(r.LoginEmail(), Is.EqualTo(Base + "/client/v1/my-game/login/email"));
            Assert.That(r.Register(), Is.EqualTo(Base + "/client/v1/my-game/register"));
            Assert.That(r.Logout(), Is.EqualTo(Base + "/client/v1/my-game/logout"));
            Assert.That(r.Me(), Is.EqualTo(Base + "/client/v1/my-game/me"));
            Assert.That(r.MeExport(), Is.EqualTo(Base + "/client/v1/my-game/me/export"));
            Assert.That(r.Data(), Is.EqualTo(Base + "/client/v1/my-game/data"));
            Assert.That(r.Data(new[] { "coins", "level" }), Is.EqualTo(Base + "/client/v1/my-game/data?keys=coins%2Clevel"));
            Assert.That(r.Data(new string[0]), Is.EqualTo(Base + "/client/v1/my-game/data"));
            Assert.That(r.PlayerData("pl_abc"), Is.EqualTo(Base + "/client/v1/my-game/players/pl_abc/data"));
            Assert.That(r.TitleData(new[] { "motd" }), Is.EqualTo(Base + "/client/v1/my-game/title-data?keys=motd"));
            Assert.That(r.Statistics(), Is.EqualTo(Base + "/client/v1/my-game/statistics"));
            Assert.That(r.Leaderboard("score", 10, false), Is.EqualTo(Base + "/client/v1/my-game/leaderboards/score?top=10"));
            Assert.That(r.Leaderboard("score", 5, true), Is.EqualTo(Base + "/client/v1/my-game/leaderboards/score?top=5&around=me"));
            Assert.That(r.Events(), Is.EqualTo(Base + "/client/v1/my-game/events"));
            Assert.That(r.Function("grantReward"), Is.EqualTo(Base + "/client/v1/my-game/functions/grantReward"));
            Assert.That(r.Sessions(), Is.EqualTo(Base + "/client/v1/my-game/sessions"));
        }

        [Test]
        public void PathValuesCannotEscapeTheirSegment()
        {
            var r = new EnluClientRoutes(Base, "game");
            Assert.That(r.PlayerData("../admin"), Is.EqualTo(Base + "/client/v1/game/players/..%2Fadmin/data"));
            Assert.Throws<ArgumentException>(() => r.Data(new[] { "a,b" }));
        }

        [Test]
        public void BuildsServerRoutes()
        {
            var r = new EnluServerRoutes(Base, "app_abc123");
            Assert.That(r.Allocations(), Is.EqualTo(Base + "/v1/apps/app_abc123/allocations"));
            Assert.That(r.Allocation("match:42"), Is.EqualTo(Base + "/v1/apps/app_abc123/allocations/match%3A42"));
            Assert.That(r.Player("pl_x"), Is.EqualTo(Base + "/v1/apps/app_abc123/players/pl_x"));
            Assert.That(r.PlayerData("pl_x"), Is.EqualTo(Base + "/v1/apps/app_abc123/players/pl_x/data"));
            Assert.That(r.PlayerStatistics("pl_x"), Is.EqualTo(Base + "/v1/apps/app_abc123/players/pl_x/statistics"));
            Assert.That(r.TitleData(), Is.EqualTo(Base + "/v1/apps/app_abc123/title-data"));
            Assert.That(r.Leaderboard("score", 10, null, null), Is.EqualTo(Base + "/v1/apps/app_abc123/leaderboards/score?top=10"));
            Assert.That(r.Leaderboard("score", 3, "pl_x", 2), Is.EqualTo(Base + "/v1/apps/app_abc123/leaderboards/score?top=3&around=pl_x&version=2"));
            Assert.That(r.Events(), Is.EqualTo(Base + "/v1/apps/app_abc123/events"));
            Assert.That(r.Function("onMatchEnd"), Is.EqualTo(Base + "/v1/apps/app_abc123/functions/onMatchEnd"));
        }

        [Test]
        public void RequiresAnApp()
        {
            Assert.Throws<ArgumentException>(() => new EnluClientRoutes(Base, " "));
            Assert.Throws<ArgumentException>(() => new EnluServerRoutes(Base, ""));
        }

        [TestCase("player_1", true)]
        [TestCase("a.b:c@d-e", true)]
        [TestCase("has space", false)]
        [TestCase("", false)]
        [TestCase("slash/no", false)]
        public void ValidatesCustomIds(string id, bool valid)
        {
            Assert.That(EnluIds.IsValidCustomId(id), Is.EqualTo(valid));
        }

        [Test]
        public void BuildsDeviceCustomIds()
        {
            Assert.That(EnluIds.DeviceCustomId("6F9619FF-8B86-D011-B42D-00C04FC964FF"), Is.EqualTo("device:6F9619FF-8B86-D011-B42D-00C04FC964FF"));
            Assert.That(EnluIds.DeviceCustomId("a b/c{d}"), Is.EqualTo("device:abcd"));
            Assert.That(EnluIds.DeviceCustomId("  "), Is.Null);
            Assert.That(EnluIds.DeviceCustomId("///"), Is.Null);
            var longId = EnluIds.DeviceCustomId(new string('a', 500))!;
            Assert.That(longId.Length, Is.EqualTo(EnluIds.MaxCustomIdLength));
            Assert.That(EnluIds.IsValidCustomId(longId), Is.True);
        }

        [TestCase("score", true)]
        [TestCase("kills.total-v2", true)]
        [TestCase("2fast", false)]
        [TestCase("bad name", false)]
        public void ValidatesNames(string name, bool valid)
        {
            Assert.That(EnluIds.IsValidName(name), Is.EqualTo(valid));
        }
    }
}
