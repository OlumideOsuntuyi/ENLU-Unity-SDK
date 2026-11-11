#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Enlu
{
    internal static class EconomyJson
    {
        public static Dictionary<string, long> CurrencyMap(Dictionary<string, object?>? o)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            if (o == null) return map;
            foreach (var kv in o) if (EnluJson.IsNumber(kv.Value)) map[kv.Key] = EnluJson.ToLong(kv.Value);
            return map;
        }

        public static int? NullableInt(Dictionary<string, object?>? o, string key) =>
            EnluJson.IsNumber(EnluJson.Get(o, key)) ? EnluJson.GetInt(o, key) : (int?)null;

        public static long? NullableLong(Dictionary<string, object?>? o, string key) =>
            EnluJson.IsNumber(EnluJson.Get(o, key)) ? EnluJson.GetLong(o, key) : (long?)null;

        public static List<ItemInstance> Instances(List<object?>? arr)
        {
            var list = new List<ItemInstance>();
            if (arr == null) return list;
            foreach (var x in arr) if (x is Dictionary<string, object?>) list.Add(ItemInstance.FromJson(x));
            return list;
        }

        public static List<ItemCount> ItemCounts(List<object?>? arr)
        {
            var list = new List<ItemCount>();
            if (arr == null) return list;
            foreach (var x in arr)
            {
                var o = EnluJson.AsObject(x);
                if (o == null) continue;
                list.Add(new ItemCount(EnluJson.GetString(o, "itemId") ?? string.Empty, EnluJson.GetInt(o, "count", 1)));
            }
            return list;
        }

        public static EnluException Bad(string what) => new EnluException("bad_response", "Unexpected " + what + " response from the server.", 200);
    }

    /// <summary>An item and a count, used in grants, bundles and rewards.</summary>
    public sealed class ItemCount
    {
        /// <summary>Catalog item id.</summary>
        public string ItemId { get; }
        /// <summary>How many.</summary>
        public int Count { get; }

        /// <summary>Creates the pair.</summary>
        public ItemCount(string itemId, int count = 1)
        {
            ItemId = itemId ?? throw new ArgumentNullException(nameof(itemId));
            Count = count;
        }

        internal Dictionary<string, object?> ToJsonObject() => new Dictionary<string, object?> { ["itemId"] = ItemId, ["count"] = Count };

        /// <inheritdoc />
        public override string ToString() => ItemId + " x" + Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Items and currencies granted together (a bundle's contents or an achievement's rewards).</summary>
    public sealed class EconomyContents
    {
        /// <summary>Items.</summary>
        public List<ItemCount> Items { get; private set; } = new List<ItemCount>();
        /// <summary>Currency amounts by code.</summary>
        public Dictionary<string, long> Currencies { get; private set; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Reads <c>{ items: [{itemId, count}], currencies: { CODE: n } }</c>; null when the node is not an object.</summary>
        public static EconomyContents? FromJson(object? node)
        {
            var o = EnluJson.AsObject(node);
            if (o == null) return null;
            return new EconomyContents
            {
                Items = EconomyJson.ItemCounts(EnluJson.GetArray(o, "items")),
                Currencies = EconomyJson.CurrencyMap(EnluJson.GetObject(o, "currencies")),
            };
        }
    }

    /// <summary>
    /// A virtual currency. Amounts are whole numbers. Currencies with a recharge rate (energy, lives)
    /// refill by <see cref="RechargeRate"/> per hour up to <see cref="RechargeMax"/>.
    /// </summary>
    public sealed class Currency
    {
        /// <summary>Code, 2 to 8 upper-case letters or digits, e.g. <c>GD</c>.</summary>
        public string Code { get; private set; } = string.Empty;
        /// <summary>Display name.</summary>
        public string Name { get; private set; } = string.Empty;
        /// <summary>Balance every new player starts with.</summary>
        public long Initial { get; private set; }
        /// <summary>Balance cap, or null.</summary>
        public long? Max { get; private set; }
        /// <summary>Units added per hour, or null.</summary>
        public long? RechargeRate { get; private set; }
        /// <summary>Recharge stops at this balance, or null.</summary>
        public long? RechargeMax { get; private set; }

        /// <summary>Reads a currency.</summary>
        public static Currency FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("currency");
            return new Currency
            {
                Code = EnluJson.GetString(o, "code") ?? string.Empty,
                Name = EnluJson.GetString(o, "name") ?? string.Empty,
                Initial = EnluJson.GetLong(o, "initial"),
                Max = EconomyJson.NullableLong(o, "max"),
                RechargeRate = EconomyJson.NullableLong(o, "rechargeRate"),
                RechargeMax = EconomyJson.NullableLong(o, "rechargeMax"),
            };
        }

        /// <summary>Reads <c>{ currencies: [...] }</c>.</summary>
        public static List<Currency> ListFromResponse(object? node)
        {
            var list = new List<Currency>();
            var arr = EnluJson.GetArray(EnluJson.AsObject(node), "currencies");
            if (arr != null) foreach (var x in arr) if (x is Dictionary<string, object?>) list.Add(FromJson(x));
            return list;
        }

        /// <inheritdoc />
        public override string ToString() => Code + " (" + Name + ")";
    }

    /// <summary>An item for sale or to grant, from the catalog (or a store, with the store's prices).</summary>
    public sealed class CatalogItem
    {
        /// <summary>Item id, e.g. <c>sword.iron</c>.</summary>
        public string ItemId { get; private set; } = string.Empty;
        /// <summary>Display name.</summary>
        public string Name { get; private set; } = string.Empty;
        /// <summary>Description.</summary>
        public string Description { get; private set; } = string.Empty;
        /// <summary>Item class, e.g. <c>weapon</c>.</summary>
        public string ItemClass { get; private set; } = string.Empty;
        /// <summary>Tags.</summary>
        public List<string> Tags { get; private set; } = new List<string>();
        /// <summary>Prices by currency code. Empty for items that are not for sale.</summary>
        public Dictionary<string, long> Prices { get; private set; } = new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>Stacks into one inventory instance.</summary>
        public bool Stackable { get; private set; }
        /// <summary>Uses per instance, or null.</summary>
        public int? Uses { get; private set; }
        /// <summary>Lifetime in seconds after it is acquired, or null.</summary>
        public long? ExpiresSeconds { get; private set; }
        /// <summary>When set, buying grants these contents instead of the item itself.</summary>
        public EconomyContents? Bundle { get; private set; }
        /// <summary>How many one player may ever buy, or null.</summary>
        public int? LimitPerPlayer { get; private set; }
        /// <summary>The client may consume it (<c>ConsumeItemAsync</c>).</summary>
        public bool Consumable { get; private set; }
        /// <summary>Your custom data (parsed JSON).</summary>
        public Dictionary<string, object?> Data { get; private set; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        /// <summary>On sale.</summary>
        public bool Enabled { get; private set; } = true;

        /// <summary>The price in a currency, or null when it cannot be bought with it.</summary>
        public long? GetPrice(string currency) => Prices.TryGetValue(currency, out var p) ? p : (long?)null;

        /// <summary>Reads a catalog item.</summary>
        public static CatalogItem FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("catalog item");
            return new CatalogItem
            {
                ItemId = EnluJson.GetString(o, "itemId") ?? string.Empty,
                Name = EnluJson.GetString(o, "name") ?? string.Empty,
                Description = EnluJson.GetString(o, "description") ?? string.Empty,
                ItemClass = EnluJson.GetString(o, "class") ?? string.Empty,
                Tags = EnluJson.GetStringList(o, "tags"),
                Prices = EconomyJson.CurrencyMap(EnluJson.GetObject(o, "prices")),
                Stackable = EnluJson.GetBool(o, "stackable"),
                Uses = EconomyJson.NullableInt(o, "uses"),
                ExpiresSeconds = EconomyJson.NullableLong(o, "expiresSeconds"),
                Bundle = EconomyContents.FromJson(EnluJson.Get(o, "bundle")),
                LimitPerPlayer = EconomyJson.NullableInt(o, "limitPerPlayer"),
                Consumable = EnluJson.GetBool(o, "consumable"),
                Data = EnluJson.GetObject(o, "data") ?? new Dictionary<string, object?>(StringComparer.Ordinal),
                Enabled = EnluJson.GetBool(o, "enabled", true),
            };
        }

        /// <summary>Reads <c>{ items: [...] }</c>.</summary>
        public static List<CatalogItem> ListFromResponse(object? node)
        {
            var list = new List<CatalogItem>();
            var arr = EnluJson.GetArray(EnluJson.AsObject(node), "items");
            if (arr != null) foreach (var x in arr) if (x is Dictionary<string, object?>) list.Add(FromJson(x));
            return list;
        }

        /// <inheritdoc />
        public override string ToString() => ItemId + " (" + Name + ")";
    }

    /// <summary>An item a player owns.</summary>
    public sealed class ItemInstance
    {
        /// <summary>Instance id (<c>ii_…</c>), used to consume, transfer or revoke it.</summary>
        public string InstanceId { get; private set; } = string.Empty;
        /// <summary>Catalog item id.</summary>
        public string ItemId { get; private set; } = string.Empty;
        /// <summary>Stack size.</summary>
        public int Count { get; private set; } = 1;
        /// <summary>Uses left, or null for items without uses.</summary>
        public int? UsesLeft { get; private set; }
        /// <summary>When acquired (UTC).</summary>
        public DateTime? AcquiredAt { get; private set; }
        /// <summary>When it expires (UTC), or null.</summary>
        public DateTime? ExpiresAt { get; private set; }
        /// <summary>Instance data (parsed JSON).</summary>
        public Dictionary<string, object?> Data { get; private set; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <summary>Reads an instance.</summary>
        public static ItemInstance FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("inventory item");
            return new ItemInstance
            {
                InstanceId = EnluJson.GetString(o, "instanceId") ?? string.Empty,
                ItemId = EnluJson.GetString(o, "itemId") ?? string.Empty,
                Count = EnluJson.GetInt(o, "count", 1),
                UsesLeft = EconomyJson.NullableInt(o, "usesLeft"),
                AcquiredAt = EnluJson.ToDateTime(EnluJson.Get(o, "acquiredAt")),
                ExpiresAt = EnluJson.ToDateTime(EnluJson.Get(o, "expiresAt")),
                Data = EnluJson.GetObject(o, "data") ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            };
        }

        /// <inheritdoc />
        public override string ToString() => ItemId + " x" + Count.ToString(CultureInfo.InvariantCulture) +
            (UsesLeft.HasValue ? " (" + UsesLeft.Value.ToString(CultureInfo.InvariantCulture) + " uses left)" : "");
    }

    /// <summary>A player's items and currency balances.</summary>
    public sealed class Inventory
    {
        /// <summary>Owned items (expired ones are already removed).</summary>
        public List<ItemInstance> Items { get; private set; } = new List<ItemInstance>();
        /// <summary>Balances by currency code (recharge applied).</summary>
        public Dictionary<string, long> Currencies { get; private set; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>The balance of a currency (0 when unknown).</summary>
        public long GetBalance(string code) => Currencies.TryGetValue(code, out var v) ? v : 0;

        /// <summary>Total count owned of an item across instances.</summary>
        public int CountOf(string itemId)
        {
            var n = 0;
            foreach (var i in Items) if (i.ItemId == itemId) n += i.Count;
            return n;
        }

        /// <summary>The first instance of an item, or null.</summary>
        public ItemInstance? Find(string itemId)
        {
            foreach (var i in Items) if (i.ItemId == itemId) return i;
            return null;
        }

        /// <summary>Reads <c>{ items, currencies }</c>.</summary>
        public static Inventory FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("inventory");
            return new Inventory
            {
                Items = EconomyJson.Instances(EnluJson.GetArray(o, "items")),
                Currencies = EconomyJson.CurrencyMap(EnluJson.GetObject(o, "currencies")),
            };
        }
    }

    /// <summary>The result of a purchase or a grant.</summary>
    public sealed class PurchaseResult
    {
        /// <summary>Instances added or increased (a bundle lists its contents).</summary>
        public List<ItemInstance> Granted { get; private set; } = new List<ItemInstance>();
        /// <summary>Balances after the change, by currency code.</summary>
        public Dictionary<string, long> Currencies { get; private set; } = new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>Amounts not granted because a currency hit its <c>max</c> (grants only).</summary>
        public Dictionary<string, long> Clipped { get; private set; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Reads <c>{ granted, currencies, clipped? }</c>.</summary>
        public static PurchaseResult FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("purchase");
            return new PurchaseResult
            {
                Granted = EconomyJson.Instances(EnluJson.GetArray(o, "granted")),
                Currencies = EconomyJson.CurrencyMap(EnluJson.GetObject(o, "currencies")),
                Clipped = EconomyJson.CurrencyMap(EnluJson.GetObject(o, "clipped")),
            };
        }
    }

    /// <summary>The result of consuming an item.</summary>
    public sealed class ConsumeResult
    {
        /// <summary>The instance after consuming, or null when it was used up and removed.</summary>
        public ItemInstance? Instance { get; private set; }
        /// <summary>How many were consumed.</summary>
        public int Consumed { get; private set; }

        /// <summary>Reads <c>{ instance, consumed }</c>.</summary>
        public static ConsumeResult FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("consume");
            var inst = EnluJson.Get(o, "instance");
            return new ConsumeResult
            {
                Instance = inst is Dictionary<string, object?> ? ItemInstance.FromJson(inst) : null,
                Consumed = EnluJson.GetInt(o, "consumed"),
            };
        }
    }

    /// <summary>A real-money purchase record (from a store receipt).</summary>
    public sealed class ReceiptPurchase
    {
        /// <summary><c>google</c>, <c>apple</c> or <c>test</c>.</summary>
        public string Provider { get; private set; } = string.Empty;
        /// <summary>The store's transaction id (processed once per app).</summary>
        public string TransactionId { get; private set; } = string.Empty;
        /// <summary>The store SKU (the ENLU IAP product id).</summary>
        public string ProductId { get; private set; } = string.Empty;
        /// <summary>The buyer.</summary>
        public string PlayerId { get; private set; } = string.Empty;
        /// <summary><c>validated</c>, <c>rejected</c> or <c>refunded</c>.</summary>
        public string Status { get; private set; } = string.Empty;
        /// <summary>When first processed (UTC).</summary>
        public DateTime? CreatedAt { get; private set; }
        /// <summary>Why it was rejected, if it was.</summary>
        public string? Error { get; private set; }

        /// <summary>Reads a purchase record; null when the node is not an object.</summary>
        public static ReceiptPurchase? FromJson(object? node)
        {
            var o = EnluJson.AsObject(node);
            if (o == null) return null;
            return new ReceiptPurchase
            {
                Provider = EnluJson.GetString(o, "provider") ?? string.Empty,
                TransactionId = EnluJson.GetString(o, "transactionId") ?? string.Empty,
                ProductId = EnluJson.GetString(o, "productId") ?? string.Empty,
                PlayerId = EnluJson.GetString(o, "playerId") ?? string.Empty,
                Status = EnluJson.GetString(o, "status") ?? string.Empty,
                CreatedAt = EnluJson.ToDateTime(EnluJson.Get(o, "createdAt")),
                Error = EnluJson.GetString(o, "error"),
            };
        }
    }

    /// <summary>The result of validating a store receipt.</summary>
    public sealed class ReceiptResult
    {
        /// <summary><c>validated</c>, <c>rejected</c> or <c>refunded</c>.</summary>
        public string Status { get; private set; } = string.Empty;
        /// <summary>True when the receipt is valid (and was granted, now or the first time it was sent).</summary>
        public bool IsValidated => Status == "validated";
        /// <summary>True when this transaction had been processed before: nothing was granted by this call.</summary>
        public bool Duplicate { get; private set; }
        /// <summary>The purchase record.</summary>
        public ReceiptPurchase? Purchase { get; private set; }
        /// <summary>What this call granted (empty when the receipt had already been processed).</summary>
        public List<ItemInstance> Granted { get; private set; } = new List<ItemInstance>();
        /// <summary>Balances after the grant (empty for a duplicate).</summary>
        public Dictionary<string, long> Currencies { get; private set; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Reads <c>{ status, duplicate, purchase, granted, currencies }</c>.</summary>
        public static ReceiptResult FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("receipt");
            var r = new ReceiptResult
            {
                Purchase = ReceiptPurchase.FromJson(EnluJson.Get(o, "purchase")),
                Duplicate = EnluJson.GetBool(o, "duplicate"),
                Granted = EconomyJson.Instances(EnluJson.GetArray(o, "granted")),
                Currencies = EconomyJson.CurrencyMap(EnluJson.GetObject(o, "currencies")),
            };
            r.Status = EnluJson.GetString(o, "status") ?? r.Purchase?.Status ?? string.Empty;
            return r;
        }
    }

    /// <summary>A player's progress on an achievement.</summary>
    public sealed class AchievementProgress
    {
        /// <summary>Achievement id.</summary>
        public string AchievementId { get; private set; } = string.Empty;
        /// <summary>Display name.</summary>
        public string Name { get; private set; } = string.Empty;
        /// <summary>Description.</summary>
        public string Description { get; private set; } = string.Empty;
        /// <summary>Progress from 0 to 1.</summary>
        public double Progress { get; private set; }
        /// <summary>When unlocked (UTC), or null.</summary>
        public DateTime? UnlockedAt { get; private set; }
        /// <summary>True once unlocked.</summary>
        public bool IsUnlocked => UnlockedAt.HasValue || Progress >= 1;
        /// <summary>Hidden until unlocked (server API only; clients never see locked hidden ones).</summary>
        public bool Hidden { get; private set; }
        /// <summary>Rewards granted on unlock (server API only).</summary>
        public EconomyContents? Rewards { get; private set; }

        /// <summary>Reads one entry.</summary>
        public static AchievementProgress FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("achievement");
            return new AchievementProgress
            {
                AchievementId = EnluJson.GetString(o, "achievementId") ?? string.Empty,
                Name = EnluJson.GetString(o, "name") ?? string.Empty,
                Description = EnluJson.GetString(o, "description") ?? string.Empty,
                Progress = Math.Max(0, Math.Min(1, EnluJson.GetDouble(o, "progress"))),
                UnlockedAt = EnluJson.ToDateTime(EnluJson.Get(o, "unlockedAt")),
                Hidden = EnluJson.GetBool(o, "hidden"),
                Rewards = EconomyContents.FromJson(EnluJson.Get(o, "rewards")),
            };
        }

        /// <summary>Reads <c>{ achievements: [...] }</c>.</summary>
        public static List<AchievementProgress> ListFromResponse(object? node)
        {
            var list = new List<AchievementProgress>();
            var arr = EnluJson.GetArray(EnluJson.AsObject(node), "achievements");
            if (arr != null) foreach (var x in arr) if (x is Dictionary<string, object?>) list.Add(FromJson(x));
            return list;
        }

        /// <inheritdoc />
        public override string ToString() => Name + " " + (Progress * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>A server-side currency change.</summary>
    public sealed class CurrencyChange
    {
        /// <summary>Currency code.</summary>
        public string Code { get; private set; } = string.Empty;
        /// <summary>Balance after the change.</summary>
        public long Balance { get; private set; }
        /// <summary>Amount not added because of the currency's max, or 0.</summary>
        public long Clipped { get; private set; }

        /// <summary>Reads <c>{ code, balance, clipped? }</c>.</summary>
        public static CurrencyChange FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("currency change");
            return new CurrencyChange
            {
                Code = EnluJson.GetString(o, "code") ?? string.Empty,
                Balance = EnluJson.GetLong(o, "balance"),
                Clipped = EnluJson.GetLong(o, "clipped"),
            };
        }
    }

    /// <summary>The result of a server-side transfer.</summary>
    public sealed class TransferResult
    {
        /// <summary>The sender's instance afterwards, or null when it was all transferred.</summary>
        public ItemInstance? From { get; private set; }
        /// <summary>The receiver's instance.</summary>
        public ItemInstance? To { get; private set; }

        /// <summary>Reads <c>{ from, to }</c>.</summary>
        public static TransferResult FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("transfer");
            var from = EnluJson.Get(o, "from");
            var to = EnluJson.Get(o, "to");
            return new TransferResult
            {
                From = from is Dictionary<string, object?> ? ItemInstance.FromJson(from) : null,
                To = to is Dictionary<string, object?> ? ItemInstance.FromJson(to) : null,
            };
        }
    }

    /// <summary>The result of unlocking an achievement from the server.</summary>
    public sealed class AchievementUnlockResult
    {
        /// <summary>True when this call unlocked it (false if it already was).</summary>
        public bool Unlocked { get; private set; }
        /// <summary>Reward items granted by this call (empty when it was already unlocked or has no rewards).</summary>
        public List<ItemInstance> Granted { get; private set; } = new List<ItemInstance>();
        /// <summary>Balances after the reward grant (empty when nothing was granted).</summary>
        public Dictionary<string, long> Currencies { get; private set; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Reads <c>{ unlocked, rewards: { granted, currencies } | null }</c>.</summary>
        public static AchievementUnlockResult FromJson(object? node)
        {
            var o = EnluJson.AsObject(node) ?? throw EconomyJson.Bad("unlock");
            var rewards = EnluJson.GetObject(o, "rewards");
            return new AchievementUnlockResult
            {
                Unlocked = EnluJson.GetBool(o, "unlocked"),
                Granted = EconomyJson.Instances(EnluJson.GetArray(rewards, "granted")),
                Currencies = EconomyJson.CurrencyMap(EnluJson.GetObject(rewards, "currencies")),
            };
        }
    }

    /// <summary>
    /// Reads Unity IAP's unified receipt (<c>{"Store","TransactionID","Payload"}</c>) into what
    /// <c>ValidateReceiptAsync</c> needs: Google Play sends the <c>purchaseToken</c> from the payload's
    /// purchase JSON, the App Store sends the <c>TransactionID</c>.
    /// </summary>
    public static class EnluStoreReceipt
    {
        /// <summary>Provider and receipt value for ENLU, or null when the receipt is not from a supported store.</summary>
        public static (string Provider, string Receipt)? Parse(string? unifiedReceipt)
        {
            if (string.IsNullOrEmpty(unifiedReceipt) || !EnluJson.TryParse(unifiedReceipt!, out var node) || !(node is Dictionary<string, object?> o)) return null;
            var store = EnluJson.GetString(o, "Store") ?? string.Empty;
            var transactionId = EnluJson.GetString(o, "TransactionID");
            switch (store)
            {
                case "GooglePlay":
                {
                    // Payload is a JSON string {"json": "<purchase JSON string>", "signature": …}.
                    var payload = EnluJson.GetString(o, "Payload");
                    if (payload == null || !EnluJson.TryParse(payload, out var p) || !(p is Dictionary<string, object?> po)) return null;
                    var json = EnluJson.GetString(po, "json");
                    if (json == null || !EnluJson.TryParse(json, out var purchase)) return null;
                    var token = EnluJson.GetString(EnluJson.AsObject(purchase), "purchaseToken");
                    return string.IsNullOrEmpty(token) ? ((string, string)?)null : ("google", token!);
                }
                case "AppleAppStore":
                case "MacAppStore":
                    return string.IsNullOrEmpty(transactionId) ? ((string, string)?)null : ("apple", transactionId!);
                case "fake":
                case "FakeStore":
                    return string.IsNullOrEmpty(transactionId) ? ((string, string)?)null : ("test", transactionId!);
                default:
                    return null;
            }
        }
    }
}
