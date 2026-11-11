#nullable enable
using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Enlu.Tests
{
    public sealed class EnluEconomyTests
    {
        private const string Base = "https://api.example.com";
        private const string LoginJson = "{\"sessionTicket\":\"enlu_ps_T\",\"playerId\":\"pl_1\",\"expiresAt\":\"2999-01-01T00:00:00.000Z\",\"newlyCreated\":false}";

        private const string InstanceJson =
            "{\"instanceId\":\"ii_1\",\"itemId\":\"potion.small\",\"count\":3,\"usesLeft\":null,\"acquiredAt\":\"2026-11-11T10:00:00.000Z\",\"expiresAt\":null,\"data\":{\"color\":\"red\"}}";

        private FakeTransport _transport = null!;
        private FakeScheduler _scheduler = null!;

        [SetUp]
        public void SetUp()
        {
            _transport = new FakeTransport();
            _scheduler = new FakeScheduler();
        }

        private EnluClient LoggedIn()
        {
            var c = new EnluClient(new EnluClientOptions { BaseUrl = Base, App = "game", MaxRetries = 3 }, _transport, _scheduler);
            _transport.Enqueue(200, LoginJson);
            c.LoginWithCustomIdAsync("p1").GetAwaiter().GetResult();
            _transport.Requests.Clear();
            return c;
        }

        /* --------------------------------- models --------------------------------- */

        [Test]
        public void ReadsCurrencies()
        {
            var list = Currency.ListFromResponse(EnluJson.Parse(
                "{\"currencies\":[{\"code\":\"GD\",\"name\":\"Gold\",\"initial\":100,\"max\":null,\"rechargeRate\":null,\"rechargeMax\":null}," +
                "{\"code\":\"EN\",\"name\":\"Energy\",\"initial\":5,\"max\":10,\"rechargeRate\":12,\"rechargeMax\":5}]}"));
            Assert.That(list.Count, Is.EqualTo(2));
            Assert.That(list[0].Code, Is.EqualTo("GD"));
            Assert.That(list[0].Initial, Is.EqualTo(100));
            Assert.That(list[0].Max, Is.Null);
            Assert.That(list[1].Max, Is.EqualTo(10));
            Assert.That(list[1].RechargeRate, Is.EqualTo(12));
            Assert.That(list[1].RechargeMax, Is.EqualTo(5));
        }

        [Test]
        public void ReadsCatalogItems()
        {
            var items = CatalogItem.ListFromResponse(EnluJson.Parse(
                "{\"items\":[{\"itemId\":\"sword.iron\",\"name\":\"Iron sword\",\"description\":\"Sharp\",\"class\":\"weapon\",\"tags\":[\"melee\"]," +
                "\"prices\":{\"GD\":250,\"GM\":5},\"stackable\":false,\"uses\":null,\"expiresSeconds\":null,\"bundle\":null,\"limitPerPlayer\":1," +
                "\"consumable\":false,\"data\":{\"damage\":12},\"enabled\":true}," +
                "{\"itemId\":\"pack.starter\",\"name\":\"Starter pack\",\"prices\":{\"GM\":10},\"stackable\":false,\"uses\":3,\"expiresSeconds\":86400," +
                "\"bundle\":{\"items\":[{\"itemId\":\"potion.small\",\"count\":5},{\"itemId\":\"sword.iron\"}],\"currencies\":{\"GD\":500}},\"consumable\":true,\"enabled\":false}]}"));
            var sword = items[0];
            Assert.That(sword.ItemClass, Is.EqualTo("weapon"));
            Assert.That(sword.Tags, Is.EqualTo(new[] { "melee" }));
            Assert.That(sword.GetPrice("GD"), Is.EqualTo(250));
            Assert.That(sword.GetPrice("XX"), Is.Null);
            Assert.That(sword.LimitPerPlayer, Is.EqualTo(1));
            Assert.That(sword.Bundle, Is.Null);
            Assert.That(EnluJson.GetInt(sword.Data, "damage"), Is.EqualTo(12));
            var pack = items[1];
            Assert.That(pack.Uses, Is.EqualTo(3));
            Assert.That(pack.ExpiresSeconds, Is.EqualTo(86400));
            Assert.That(pack.Consumable, Is.True);
            Assert.That(pack.Enabled, Is.False);
            Assert.That(pack.Bundle!.Items.Count, Is.EqualTo(2));
            Assert.That(pack.Bundle.Items[0].Count, Is.EqualTo(5));
            Assert.That(pack.Bundle.Items[1].Count, Is.EqualTo(1), "count defaults to 1");
            Assert.That(pack.Bundle.Currencies["GD"], Is.EqualTo(500));
        }

        [Test]
        public void ReadsInventories()
        {
            var inv = Inventory.FromJson(EnluJson.Parse(
                "{\"items\":[" + InstanceJson + ",{\"instanceId\":\"ii_2\",\"itemId\":\"potion.small\",\"count\":1,\"usesLeft\":2,\"acquiredAt\":\"2026-11-11T10:00:00.000Z\",\"expiresAt\":\"2026-11-12T10:00:00.000Z\",\"data\":{}}]," +
                "\"currencies\":{\"GD\":350,\"EN\":4}}"));
            Assert.That(inv.Items.Count, Is.EqualTo(2));
            Assert.That(inv.CountOf("potion.small"), Is.EqualTo(4));
            Assert.That(inv.CountOf("nothing"), Is.EqualTo(0));
            Assert.That(inv.GetBalance("GD"), Is.EqualTo(350));
            Assert.That(inv.GetBalance("ZZ"), Is.EqualTo(0));
            var first = inv.Find("potion.small")!;
            Assert.That(first.InstanceId, Is.EqualTo("ii_1"));
            Assert.That(first.UsesLeft, Is.Null);
            Assert.That(first.ExpiresAt, Is.Null);
            Assert.That(EnluJson.GetString(first.Data, "color"), Is.EqualTo("red"));
            Assert.That(inv.Items[1].UsesLeft, Is.EqualTo(2));
            Assert.That(inv.Items[1].ExpiresAt, Is.EqualTo(new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void ReadsPurchaseConsumeAndReceiptResults()
        {
            var p = PurchaseResult.FromJson(EnluJson.Parse("{\"granted\":[" + InstanceJson + "],\"currencies\":{\"GD\":100},\"clipped\":{\"GM\":5}}"));
            Assert.That(p.Granted[0].ItemId, Is.EqualTo("potion.small"));
            Assert.That(p.Currencies["GD"], Is.EqualTo(100));
            Assert.That(p.Clipped["GM"], Is.EqualTo(5));

            var used = ConsumeResult.FromJson(EnluJson.Parse("{\"instance\":null,\"consumed\":1}"));
            Assert.That(used.Instance, Is.Null);
            Assert.That(used.Consumed, Is.EqualTo(1));
            var left = ConsumeResult.FromJson(EnluJson.Parse("{\"instance\":" + InstanceJson + ",\"consumed\":2}"));
            Assert.That(left.Instance!.Count, Is.EqualTo(3));

            var r = ReceiptResult.FromJson(EnluJson.Parse(
                "{\"status\":\"validated\",\"duplicate\":false,\"purchase\":{\"provider\":\"test\",\"transactionId\":\"test:abc\",\"productId\":\"gems_500\"," +
                "\"playerId\":\"pl_1\",\"status\":\"validated\",\"createdAt\":\"2026-11-11T10:00:00.000Z\"},\"granted\":[],\"currencies\":{\"GM\":500}}"));
            Assert.That(r.IsValidated, Is.True);
            Assert.That(r.Duplicate, Is.False);
            Assert.That(r.Purchase!.TransactionId, Is.EqualTo("test:abc"));
            Assert.That(r.Currencies["GM"], Is.EqualTo(500));
            var dup = ReceiptResult.FromJson(EnluJson.Parse("{\"status\":\"validated\",\"duplicate\":true,\"purchase\":{\"status\":\"validated\"},\"granted\":[],\"currencies\":{}}"));
            Assert.That(dup.Duplicate, Is.True);
            var rejected = ReceiptResult.FromJson(EnluJson.Parse("{\"status\":\"rejected\",\"purchase\":{\"status\":\"rejected\",\"error\":\"Test receipt rejected.\"},\"granted\":[],\"currencies\":{}}"));
            Assert.That(rejected.IsValidated, Is.False);
            Assert.That(rejected.Purchase!.Error, Is.EqualTo("Test receipt rejected."));
        }

        [Test]
        public void ReadsAchievementsAndServerResults()
        {
            var list = AchievementProgress.ListFromResponse(EnluJson.Parse(
                "{\"achievements\":[{\"achievementId\":\"three_wins\",\"name\":\"Three wins\",\"description\":\"Win 3\",\"progress\":0.6666,\"unlockedAt\":null}," +
                "{\"achievementId\":\"secret\",\"name\":\"Secret\",\"progress\":1,\"unlockedAt\":\"2026-11-11T10:00:00.000Z\",\"hidden\":true,\"rewards\":{\"currencies\":{\"GD\":25}}}]}"));
            Assert.That(list[0].IsUnlocked, Is.False);
            Assert.That(list[0].Progress, Is.EqualTo(0.6666).Within(1e-9));
            Assert.That(list[0].ToString(), Is.EqualTo("Three wins 67%"));
            Assert.That(list[1].IsUnlocked, Is.True);
            Assert.That(list[1].Hidden, Is.True);
            Assert.That(list[1].Rewards!.Currencies["GD"], Is.EqualTo(25));

            var unlocked = AchievementUnlockResult.FromJson(EnluJson.Parse("{\"unlocked\":true,\"rewards\":{\"granted\":[" + InstanceJson + "],\"currencies\":{\"GD\":125}}}"));
            Assert.That(unlocked.Unlocked, Is.True);
            Assert.That(unlocked.Granted.Count, Is.EqualTo(1));
            Assert.That(unlocked.Currencies["GD"], Is.EqualTo(125));
            var again = AchievementUnlockResult.FromJson(EnluJson.Parse("{\"unlocked\":false,\"rewards\":null}"));
            Assert.That(again.Unlocked, Is.False);
            Assert.That(again.Granted, Is.Empty);

            var change = CurrencyChange.FromJson(EnluJson.Parse("{\"code\":\"GD\",\"balance\":90,\"clipped\":10}"));
            Assert.That(change.Balance, Is.EqualTo(90));
            Assert.That(change.Clipped, Is.EqualTo(10));
            var moved = TransferResult.FromJson(EnluJson.Parse("{\"from\":null,\"to\":" + InstanceJson + "}"));
            Assert.That(moved.From, Is.Null);
            Assert.That(moved.To!.InstanceId, Is.EqualTo("ii_1"));
        }

        /* -------------------------------- errors -------------------------------- */

        [Test]
        public void EconomyErrorHelpers()
        {
            var funds = EnluException.FromResponse(409, "{\"code\":\"insufficient_funds\",\"message\":\"Not enough GD: the balance is 10, 250 is needed.\",\"details\":{\"currency\":\"GD\",\"balance\":10,\"needed\":250}}", null);
            Assert.That(funds.IsInsufficientFunds, Is.True);
            Assert.That(funds.IsPriceChanged || funds.IsLimitReached, Is.False);
            Assert.That(funds.NewPrice, Is.Null);

            var price = EnluException.FromResponse(409, "{\"code\":\"price_changed\",\"message\":\"The price is now 300 GD.\",\"details\":{\"price\":300,\"currency\":\"GD\"}}", null);
            Assert.That(price.IsPriceChanged, Is.True);
            Assert.That(price.NewPrice, Is.EqualTo(300));

            Assert.That(EnluException.FromResponse(409, "{\"code\":\"limit_reached\",\"message\":\"x\",\"details\":{\"itemId\":\"sword\",\"limit\":1}}", null).IsLimitReached, Is.True);
            Assert.That(EnluException.FromResponse(409, "{\"code\":\"receipt_used\",\"message\":\"x\"}", null).IsReceiptUsed, Is.True);
            Assert.That(EnluException.FromResponse(409, "{\"code\":\"purchases_not_configured\",\"message\":\"x\"}", null).IsPurchasesNotConfigured, Is.True);
            Assert.That(EnluException.FromResponse(502, "{\"code\":\"store_unavailable\",\"message\":\"x\"}", null).IsStoreUnavailable, Is.True);
        }

        /* -------------------------------- requests -------------------------------- */

        [Test]
        public void ClientEconomyRequests()
        {
            var c = LoggedIn();
            _transport.Enqueue(200, "{\"items\":[]}");
            c.GetCatalogAsync("summer").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/catalog?storeId=summer"));
            _transport.Enqueue(200, "{\"items\":[]}");
            c.GetCatalogAsync().GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/catalog"));
            _transport.Enqueue(200, "{\"currencies\":[]}");
            c.GetCurrenciesAsync().GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/currencies"));
            _transport.Enqueue(200, "{\"items\":[],\"currencies\":{\"GD\":1}}");
            Assert.That(c.GetInventoryAsync().GetAwaiter().GetResult().GetBalance("GD"), Is.EqualTo(1));
            Assert.That(_transport.Last.Headers["X-Enlu-Session"], Is.EqualTo("enlu_ps_T"));

            _transport.Enqueue(200, "{\"granted\":[],\"currencies\":{\"GD\":0}}");
            c.PurchaseItemAsync("sword.iron", "GD", 250, "summer", 2).GetAwaiter().GetResult();
            Assert.That(_transport.Last.Method, Is.EqualTo("POST"));
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/purchase"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"itemId\":\"sword.iron\",\"currency\":\"GD\",\"price\":250,\"count\":2,\"storeId\":\"summer\"}"));

            _transport.Enqueue(200, "{\"instance\":null,\"consumed\":1}");
            c.ConsumeItemAsync("ii_1").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/inventory/ii_1/consume"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"count\":1}"));

            _transport.Enqueue(200, "{\"status\":\"validated\",\"granted\":[],\"currencies\":{}}");
            c.ValidateReceiptAsync("google", "gems_500", "token-abc").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/purchases/validate"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"provider\":\"google\",\"productId\":\"gems_500\",\"receipt\":\"token-abc\"}"));

            _transport.Enqueue(200, "{\"achievements\":[]}");
            c.GetAchievementsAsync().GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(Base + "/client/v1/game/achievements"));
        }

        [Test]
        public void ClientValidatesArguments()
        {
            var c = LoggedIn();
            Assert.Throws<ArgumentException>(() => c.PurchaseItemAsync("Sword", "GD", 1).GetAwaiter().GetResult());
            Assert.Throws<ArgumentException>(() => c.PurchaseItemAsync("sword", "gd", 1).GetAwaiter().GetResult());
            Assert.Throws<ArgumentException>(() => c.PurchaseItemAsync("sword", "G", 1).GetAwaiter().GetResult());
            Assert.Throws<ArgumentOutOfRangeException>(() => c.PurchaseItemAsync("sword", "GD", -1).GetAwaiter().GetResult());
            Assert.Throws<ArgumentOutOfRangeException>(() => c.PurchaseItemAsync("sword", "GD", 1, null, 0).GetAwaiter().GetResult());
            Assert.Throws<ArgumentException>(() => c.ValidateReceiptAsync("steam", "x", "y").GetAwaiter().GetResult());
            Assert.Throws<ArgumentException>(() => c.ConsumeItemAsync("").GetAwaiter().GetResult());
            Assert.That(_transport.Requests, Is.Empty);
        }

        [Test]
        public void PurchasesAreNeverRetriedAfterNetworkErrors()
        {
            var c = LoggedIn();
            _transport.EnqueueNetworkError();
            var e = Assert.Throws<EnluException>(() => c.PurchaseItemAsync("sword.iron", "GD", 250).GetAwaiter().GetResult());
            Assert.That(e!.IsNetworkError, Is.True);
            Assert.That(_transport.Requests.Count, Is.EqualTo(1));

            _transport.Requests.Clear();
            _transport.Enqueue(503, "{\"code\":\"unavailable\",\"message\":\"busy\"}");
            Assert.Throws<EnluException>(() => c.PurchaseItemAsync("sword.iron", "GD", 250).GetAwaiter().GetResult());
            Assert.That(_transport.Requests.Count, Is.EqualTo(1), "nor after a 503");

            _transport.Requests.Clear();
            _transport.EnqueueNetworkError();
            Assert.Throws<EnluException>(() => c.ConsumeItemAsync("ii_1").GetAwaiter().GetResult());
            Assert.That(_transport.Requests.Count, Is.EqualTo(1), "consume is not idempotent either");
        }

        [Test]
        public void PurchasesAreRetriedOnlyWhenRateLimited()
        {
            var c = LoggedIn();
            _transport.Enqueue(429, "{\"code\":\"rate_limited\",\"message\":\"Too many requests.\"}", "1");
            _transport.Enqueue(200, "{\"granted\":[],\"currencies\":{}}");
            c.PurchaseItemAsync("sword.iron", "GD", 250).GetAwaiter().GetResult();
            Assert.That(_transport.Requests.Count, Is.EqualTo(2));
        }

        [Test]
        public void ReceiptValidationIsRetriedAfterNetworkErrors()
        {
            var c = LoggedIn();
            _transport.EnqueueNetworkError();
            _transport.Enqueue(200, "{\"status\":\"validated\",\"duplicate\":true,\"granted\":[],\"currencies\":{}}");
            var r = c.ValidateReceiptAsync("apple", "gems_500", "2000000123456789").GetAwaiter().GetResult();
            Assert.That(r.Duplicate, Is.True);
            Assert.That(_transport.Requests.Count, Is.EqualTo(2));
        }

        [Test]
        public void PriceChangedSurfacesTheNewPrice()
        {
            var c = LoggedIn();
            _transport.Enqueue(409, "{\"code\":\"price_changed\",\"message\":\"The price is now 300 GD.\",\"details\":{\"price\":300,\"currency\":\"GD\"}}");
            var e = Assert.Throws<EnluException>(() => c.PurchaseItemAsync("sword.iron", "GD", 250).GetAwaiter().GetResult());
            Assert.That(e!.IsPriceChanged, Is.True);
            Assert.That(e.NewPrice, Is.EqualTo(300));
            Assert.That(e.HttpStatus, Is.EqualTo(409));
        }

        [Test]
        public void ServerEconomyRequests()
        {
            var api = new EnluServerApi(new EnluClientOptions { BaseUrl = Base, App = "app_1" }, "enlu_key_K", _transport, _scheduler);
            const string P = Base + "/v1/apps/app_1/players/pl_1";

            _transport.Enqueue(200, "{\"items\":[],\"currencies\":{}}");
            api.GetInventoryAsync("pl_1").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/inventory"));

            _transport.Enqueue(200, "{\"granted\":[],\"currencies\":{\"GD\":150},\"clipped\":{\"GD\":5}}");
            var g = api.GrantAsync("pl_1", new[] { new ItemCount("potion.small", 3) }, new Dictionary<string, long> { ["GD"] = 50 }, "match reward").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/inventory/grant"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"items\":[{\"itemId\":\"potion.small\",\"count\":3}],\"currencies\":{\"GD\":50},\"note\":\"match reward\"}"));
            Assert.That(g.Clipped["GD"], Is.EqualTo(5));
            Assert.Throws<ArgumentException>(() => api.GrantAsync("pl_1", null, null).GetAwaiter().GetResult());

            _transport.Enqueue(200, "{\"code\":\"GD\",\"balance\":40}");
            api.AddCurrencyAsync("pl_1", "GD", -10, "fee").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/currencies/GD"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"delta\":-10,\"note\":\"fee\"}"));

            _transport.Enqueue(200, "{\"instance\":null,\"consumed\":1}");
            api.ConsumeAsync("pl_1", "ii_1").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/inventory/ii_1/consume"));

            _transport.Enqueue(200, "{\"revoked\":2}");
            Assert.That(api.RevokeAsync("pl_1", "ii_1", 2).GetAwaiter().GetResult(), Is.EqualTo(2));
            Assert.That(_transport.Last.Method, Is.EqualTo("DELETE"));
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/inventory/ii_1?count=2"));

            _transport.Enqueue(200, "{\"from\":null,\"to\":" + InstanceJson + "}");
            api.TransferAsync("pl_1", "ii_1", "pl_2").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/inventory/ii_1/transfer"));
            Assert.That(_transport.Last.Body, Is.EqualTo("{\"toPlayerId\":\"pl_2\",\"count\":1}"));

            _transport.Enqueue(200, "{\"granted\":[],\"currencies\":{}}");
            api.PurchaseForPlayerAsync("pl_1", "sword.iron", "GD", 250).GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/purchase"));

            _transport.Enqueue(200, "{\"unlocked\":true,\"rewards\":null}");
            Assert.That(api.UnlockAchievementAsync("pl_1", "first_win").GetAwaiter().GetResult().Unlocked, Is.True);
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/achievements/first_win/unlock"));

            _transport.Enqueue(200, "{\"achievements\":[]}");
            api.GetPlayerAchievementsAsync("pl_1").GetAwaiter().GetResult();
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/achievements"));

            _transport.Enqueue(200, "{\"segments\":[\"whales\",\"new\"]}");
            Assert.That(api.GetPlayerSegmentsAsync("pl_1").GetAwaiter().GetResult(), Is.EqualTo(new[] { "whales", "new" }));
            Assert.That(_transport.Last.Url, Is.EqualTo(P + "/segments"));
            Assert.That(_transport.Last.Headers["Authorization"], Is.EqualTo("Bearer enlu_key_K"));
        }

        [Test]
        public void ServerGrantsAreNotRetriedAfterNetworkErrors()
        {
            var api = new EnluServerApi(new EnluClientOptions { BaseUrl = Base, App = "app_1" }, "enlu_key_K", _transport, _scheduler);
            _transport.EnqueueNetworkError();
            Assert.Throws<EnluException>(() => api.AddCurrencyAsync("pl_1", "GD", 10).GetAwaiter().GetResult());
            Assert.That(_transport.Requests.Count, Is.EqualTo(1));
        }

        /* ------------------------------ store receipts ------------------------------ */

        private static string Unified(string store, string transactionId, string payload) =>
            EnluJson.Serialize(new Dictionary<string, object?> { ["Store"] = store, ["TransactionID"] = transactionId, ["Payload"] = payload });

        [Test]
        public void ParsesGooglePlayReceipts()
        {
            var purchaseJson = EnluJson.Serialize(new Dictionary<string, object?>
            {
                ["orderId"] = "GPA.1234", ["packageName"] = "com.studio.game", ["productId"] = "gems_500", ["purchaseToken"] = "opaque-token-123",
            });
            var payload = EnluJson.Serialize(new Dictionary<string, object?> { ["json"] = purchaseJson, ["signature"] = "sig" });
            var parsed = EnluStoreReceipt.Parse(Unified("GooglePlay", "GPA.1234", payload));
            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.Value.Provider, Is.EqualTo("google"));
            Assert.That(parsed.Value.Receipt, Is.EqualTo("opaque-token-123"));
        }

        [Test]
        public void ParsesAppStoreAndFakeStoreReceipts()
        {
            var apple = EnluStoreReceipt.Parse(Unified("AppleAppStore", "2000000123456789", "MIIT...base64"));
            Assert.That(apple!.Value.Provider, Is.EqualTo("apple"));
            Assert.That(apple.Value.Receipt, Is.EqualTo("2000000123456789"));
            var fake = EnluStoreReceipt.Parse(Unified("fake", "fake-tx-1", "ThisIsFakeReceiptData"));
            Assert.That(fake!.Value.Provider, Is.EqualTo("test"));
            Assert.That(fake.Value.Receipt, Is.EqualTo("fake-tx-1"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{\"Store\":\"AmazonAppStore\",\"TransactionID\":\"x\",\"Payload\":\"{}\"}")]
        [TestCase("{\"Store\":\"GooglePlay\",\"TransactionID\":\"x\",\"Payload\":\"not json\"}")]
        [TestCase("{\"Store\":\"GooglePlay\",\"TransactionID\":\"x\",\"Payload\":\"{\\\"json\\\":\\\"{}\\\"}\"}")]
        [TestCase("{\"Store\":\"AppleAppStore\",\"Payload\":\"x\"}")]
        public void RejectsUnsupportedOrBrokenReceipts(string? receipt)
        {
            Assert.That(EnluStoreReceipt.Parse(receipt), Is.Null);
        }

        [Test]
        public void EconomyRoutes()
        {
            var r = new EnluServerRoutes(Base, "app_1");
            Assert.That(r.Revoke("pl_1", "ii_1", null), Is.EqualTo(Base + "/v1/apps/app_1/players/pl_1/inventory/ii_1"));
            Assert.That(r.PlayerCurrency("pl_1", "GD"), Is.EqualTo(Base + "/v1/apps/app_1/players/pl_1/currencies/GD"));
            var c = new EnluClientRoutes(Base, "game");
            Assert.That(c.ValidateReceipt(), Is.EqualTo(Base + "/client/v1/game/purchases/validate"));
            Assert.That(c.Consume("ii_9"), Is.EqualTo(Base + "/client/v1/game/inventory/ii_9/consume"));
        }
    }
}
