#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu
{
    public sealed partial class EnluServerApi
    {
        /* -------------------------------- economy -------------------------------- */
        // These need an API key with the economy scope. Calls that change balances or items are not
        // idempotent, so they are retried only on 429 (rejected before the server acted).

        /// <summary>A player's items and currency balances.</summary>
        public async Task<Inventory> GetInventoryAsync(string playerId, CancellationToken cancellationToken = default) =>
            Inventory.FromJson(await Call("GET", Routes.Inventory(playerId), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>
        /// Grants items and/or currencies (rewards, compensation, server-side loot). Currency beyond a
        /// currency's <c>max</c> is clipped and reported in <see cref="PurchaseResult.Clipped"/>.
        /// </summary>
        public async Task<PurchaseResult> GrantAsync(string playerId, IEnumerable<ItemCount>? items, IDictionary<string, long>? currencies, string? note = null,
            CancellationToken cancellationToken = default)
        {
            var body = new Dictionary<string, object?>();
            if (items != null)
            {
                var list = new List<object?>();
                foreach (var i in items)
                {
                    EnluClient.ValidateItemId(i.ItemId);
                    list.Add(i.ToJsonObject());
                }
                if (list.Count > 0) body["items"] = list;
            }
            if (currencies != null && currencies.Count > 0)
            {
                foreach (var code in currencies.Keys) EnluClient.ValidateCurrencyCode(code);
                body["currencies"] = currencies;
            }
            if (body.Count == 0) throw new ArgumentException("Grant at least one item or currency.");
            if (!string.IsNullOrEmpty(note)) body["note"] = note;
            return PurchaseResult.FromJson(await Call("POST", Routes.Grant(playerId), body, EnluRetryMode.RejectedOnly, cancellationToken));
        }

        /// <summary>
        /// Adds (positive) or removes (negative) currency. Fails with 409 <c>insufficient_funds</c> when
        /// the balance would go below zero.
        /// </summary>
        public async Task<CurrencyChange> AddCurrencyAsync(string playerId, string code, long delta, string? note = null, CancellationToken cancellationToken = default)
        {
            EnluClient.ValidateCurrencyCode(code);
            var body = new Dictionary<string, object?> { ["delta"] = delta };
            if (!string.IsNullOrEmpty(note)) body["note"] = note;
            return CurrencyChange.FromJson(await Call("POST", Routes.PlayerCurrency(playerId, code), body, EnluRetryMode.RejectedOnly, cancellationToken));
        }

        /// <summary>Consumes an item on the player's behalf (any item, consumable or not).</summary>
        public async Task<ConsumeResult> ConsumeAsync(string playerId, string instanceId, int count = 1, CancellationToken cancellationToken = default) =>
            ConsumeResult.FromJson(await Call("POST", Routes.Consume(playerId, instanceId), new Dictionary<string, object?> { ["count"] = count },
                EnluRetryMode.RejectedOnly, cancellationToken));

        /// <summary>Removes an item (all of it, or <paramref name="count"/> from a stack). Returns how many were revoked.</summary>
        public async Task<int> RevokeAsync(string playerId, string instanceId, int? count = null, CancellationToken cancellationToken = default)
        {
            var node = await Call("DELETE", Routes.Revoke(playerId, instanceId, count), null, EnluRetryMode.RejectedOnly, cancellationToken);
            return EnluJson.GetInt(EnluJson.AsObject(node), "revoked");
        }

        /// <summary>Moves an item (or part of a stack) to another player (trading, gifting).</summary>
        public async Task<TransferResult> TransferAsync(string fromPlayerId, string instanceId, string toPlayerId, int count = 1,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(toPlayerId)) throw new ArgumentException("toPlayerId is required.", nameof(toPlayerId));
            var body = new Dictionary<string, object?> { ["toPlayerId"] = toPlayerId, ["count"] = count };
            return TransferResult.FromJson(await Call("POST", Routes.Transfer(fromPlayerId, instanceId), body, EnluRetryMode.RejectedOnly, cancellationToken));
        }

        /// <summary>
        /// Buys an item for a player with their virtual currency (same rules and errors as the client's
        /// <c>PurchaseItemAsync</c>). Not retried after network errors: it is not idempotent.
        /// </summary>
        public async Task<PurchaseResult> PurchaseForPlayerAsync(string playerId, string itemId, string currency, long price, string? storeId = null, int count = 1,
            CancellationToken cancellationToken = default)
        {
            EnluClient.ValidateItemId(itemId);
            EnluClient.ValidateCurrencyCode(currency);
            var body = new Dictionary<string, object?> { ["itemId"] = itemId, ["currency"] = currency, ["price"] = price, ["count"] = count };
            if (!string.IsNullOrEmpty(storeId)) body["storeId"] = storeId;
            return PurchaseResult.FromJson(await Call("POST", Routes.PlayerPurchase(playerId), body, EnluRetryMode.RejectedOnly, cancellationToken));
        }

        /// <summary>Unlocks an achievement (once; rewards are granted the first time). Safe to repeat.</summary>
        public async Task<AchievementUnlockResult> UnlockAchievementAsync(string playerId, string achievementId, CancellationToken cancellationToken = default) =>
            AchievementUnlockResult.FromJson(await Call("POST", Routes.UnlockAchievement(playerId, achievementId), new Dictionary<string, object?>(),
                EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>A player's achievements with progress, including hidden ones.</summary>
        public async Task<List<AchievementProgress>> GetPlayerAchievementsAsync(string playerId, CancellationToken cancellationToken = default) =>
            AchievementProgress.ListFromResponse(await Call("GET", Routes.PlayerAchievements(playerId), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>The segment ids a player is in right now (e.g. <c>whales</c>, <c>lapsed</c>).</summary>
        public async Task<List<string>> GetPlayerSegmentsAsync(string playerId, CancellationToken cancellationToken = default)
        {
            var node = await Call("GET", Routes.PlayerSegments(playerId), null, EnluRetryMode.Idempotent, cancellationToken);
            return EnluJson.GetStringList(EnluJson.AsObject(node), "segments");
        }
    }
}
