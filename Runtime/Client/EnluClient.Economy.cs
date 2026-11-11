#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Enlu
{
    public sealed partial class EnluClient
    {
        /* -------------------------------- economy -------------------------------- */

        /// <summary>
        /// The items on sale: the whole enabled catalog, or one store's items with the store's prices
        /// (sales) when <paramref name="storeId"/> is given.
        /// </summary>
        public async Task<List<CatalogItem>> GetCatalogAsync(string? storeId = null, CancellationToken cancellationToken = default) =>
            CatalogItem.ListFromResponse(await PlayerCall("GET", Routes.Catalog(storeId), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>The app's virtual currencies.</summary>
        public async Task<List<Currency>> GetCurrenciesAsync(CancellationToken cancellationToken = default) =>
            Currency.ListFromResponse(await PlayerCall("GET", Routes.Currencies(), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>The player's items and currency balances (recharging currencies are up to date).</summary>
        public async Task<Inventory> GetInventoryAsync(CancellationToken cancellationToken = default) =>
            Inventory.FromJson(await PlayerCall("GET", Routes.Inventory(), null, EnluRetryMode.Idempotent, cancellationToken));

        /// <summary>
        /// Buys a catalog item with virtual currency. Pass the price the player saw: if it changed,
        /// the server refuses with <c>price_changed</c> (<see cref="EnluException.NewPrice"/>) instead of
        /// charging a different amount.
        /// </summary>
        /// <remarks>
        /// <para>Purchases are <b>not idempotent</b>, so this call is <b>never retried automatically</b>
        /// after a network error or a 503: the purchase may have gone through. Only a 429, which the
        /// server rejects before acting, is retried. After a network error, reload the inventory with
        /// <see cref="GetInventoryAsync"/> to see whether the item arrived before offering to buy again.</para>
        /// <para>Errors: 409 <c>insufficient_funds</c> (<see cref="EnluException.IsInsufficientFunds"/>),
        /// 409 <c>price_changed</c>, 409 <c>limit_reached</c>, 404 when the item is not for sale.</para>
        /// </remarks>
        public async Task<PurchaseResult> PurchaseItemAsync(string itemId, string currency, long price, string? storeId = null, int count = 1,
            CancellationToken cancellationToken = default)
        {
            ValidateItemId(itemId);
            ValidateCurrencyCode(currency);
            if (price < 0) throw new ArgumentOutOfRangeException(nameof(price), "Prices are never negative.");
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "Buy at least one.");
            var body = new Dictionary<string, object?> { ["itemId"] = itemId, ["currency"] = currency, ["price"] = price, ["count"] = count };
            if (!string.IsNullOrEmpty(storeId)) body["storeId"] = storeId;
            return PurchaseResult.FromJson(await PlayerCall("POST", Routes.Purchase(), body, EnluRetryMode.RejectedOnly, cancellationToken));
        }

        /// <summary>
        /// Uses up a consumable item: reduces a stack's count, or an item's uses left. Not retried after
        /// network errors (it is not idempotent). Fails with 403 when the item is not consumable.
        /// </summary>
        public async Task<ConsumeResult> ConsumeItemAsync(string instanceId, int count = 1, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(instanceId)) throw new ArgumentException("instanceId is required.", nameof(instanceId));
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "Consume at least one.");
            return ConsumeResult.FromJson(await PlayerCall("POST", Routes.Consume(instanceId),
                new Dictionary<string, object?> { ["count"] = count }, EnluRetryMode.RejectedOnly, cancellationToken));
        }

        /// <summary>
        /// Validates a real-money store receipt and grants the matching IAP product's contents.
        /// <paramref name="provider"/> is <c>google</c> (receipt = purchase token), <c>apple</c>
        /// (receipt = transaction id) or <c>test</c> (any string, when the app enables the test provider;
        /// strings starting with <c>fail</c> are rejected). <paramref name="productId"/> is the store SKU,
        /// registered in ENLU as an IAP product.
        /// </summary>
        /// <remarks>
        /// <para>Safe to retry, and retried automatically after network errors: the server processes each
        /// transaction id once, so re-sending a validated receipt returns the same result
        /// (<see cref="ReceiptResult.Duplicate"/>) and grants nothing twice. With Unity IAP, confirm the
        /// pending purchase only after this returns <see cref="ReceiptResult.IsValidated"/> (see the
        /// <c>Enlu.Purchasing</c> helper).</para>
        /// <para>Errors: 409 <c>receipt_used</c> (claimed by another player), 409 <c>purchases_not_configured</c>,
        /// 502 <c>store_unavailable</c> (try later), 404 for an unknown product, 403 when the test provider is off.</para>
        /// </remarks>
        public async Task<ReceiptResult> ValidateReceiptAsync(string provider, string productId, string receipt, CancellationToken cancellationToken = default)
        {
            if (provider != "google" && provider != "apple" && provider != "test")
                throw new ArgumentException("provider must be google, apple or test.", nameof(provider));
            if (string.IsNullOrEmpty(productId)) throw new ArgumentException("productId is required.", nameof(productId));
            if (string.IsNullOrEmpty(receipt)) throw new ArgumentException("receipt is required.", nameof(receipt));
            var body = new Dictionary<string, object?> { ["provider"] = provider, ["productId"] = productId, ["receipt"] = receipt };
            return ReceiptResult.FromJson(await PlayerCall("POST", Routes.ValidateReceipt(), body, EnluRetryMode.Idempotent, cancellationToken));
        }

        /// <summary>The player's achievements with progress (hidden ones appear once unlocked).</summary>
        public async Task<List<AchievementProgress>> GetAchievementsAsync(CancellationToken cancellationToken = default) =>
            AchievementProgress.ListFromResponse(await PlayerCall("GET", Routes.Achievements(), null, EnluRetryMode.Idempotent, cancellationToken));

        internal static void ValidateItemId(string itemId)
        {
            if (string.IsNullOrEmpty(itemId) || itemId.Length > 64 || !(itemId[0] >= 'a' && itemId[0] <= 'z'))
                throw new ArgumentException("Item ids start with a lower-case letter and use a-z, 0-9, _ . - (up to 64): " + itemId, nameof(itemId));
            foreach (var c in itemId)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'))
                    throw new ArgumentException("Item ids use a-z, 0-9, _ . -: " + itemId, nameof(itemId));
        }

        internal static void ValidateCurrencyCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 2 || code.Length > 8 || !(code[0] >= 'A' && code[0] <= 'Z'))
                throw new ArgumentException("Currency codes are 2 to 8 upper-case letters or digits, starting with a letter: " + code, nameof(code));
            foreach (var c in code)
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                    throw new ArgumentException("Currency codes are upper-case letters and digits: " + code, nameof(code));
        }
    }
}
