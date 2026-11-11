#nullable enable
#if ENLU_UNITY_PURCHASING
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Purchasing;

namespace Enlu.Purchasing
{
    /// <summary>
    /// Connects Unity IAP (com.unity.purchasing 4.x) to ENLU receipt validation. This assembly is
    /// compiled only when the Unity IAP package is installed.
    /// </summary>
    /// <remarks>
    /// <para>Recommended flow: return <see cref="PurchaseProcessingResult.Pending"/> from
    /// <c>ProcessPurchase</c>, validate with ENLU, and confirm the pending purchase only once ENLU
    /// says <c>validated</c>:</para>
    /// <code>
    /// public PurchaseProcessingResult ProcessPurchase(PurchaseEventArgs e)
    /// {
    ///     _ = EnluClient.Default.ValidateAndConfirmAsync(controller, e.purchasedProduct);
    ///     return PurchaseProcessingResult.Pending;
    /// }
    /// </code>
    /// <para>If validation fails on the network, the purchase stays pending and Unity IAP delivers it
    /// again on the next launch; ENLU processes each transaction once, so validating again is safe.</para>
    /// </remarks>
    public static class EnluPurchasing
    {
        /// <summary>
        /// Sends the product's store receipt to ENLU: for Google Play the <c>purchaseToken</c> from the
        /// receipt's purchase JSON, for the App Store the <c>transactionID</c>, for Unity's fake store
        /// the <c>test</c> provider.
        /// </summary>
        /// <param name="client">A logged-in client.</param>
        /// <param name="product">The purchased product (with a receipt).</param>
        /// <param name="enluProductId">The ENLU IAP product id; defaults to the store SKU (<c>definition.storeSpecificId</c>).</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public static Task<ReceiptResult> ValidateAsync(this EnluClient client, Product product, string? enluProductId = null,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (product == null) throw new ArgumentNullException(nameof(product));
            if (!product.hasReceipt || string.IsNullOrEmpty(product.receipt)) throw new ArgumentException("The product has no receipt yet.", nameof(product));
            var parsed = EnluStoreReceipt.Parse(product.receipt)
                ?? throw new NotSupportedException("ENLU validates Google Play and App Store receipts (and Unity's fake store as 'test'). This receipt is from another store.");
            return client.ValidateReceiptAsync(parsed.Provider, enluProductId ?? product.definition.storeSpecificId, parsed.Receipt, cancellationToken);
        }

        /// <summary>
        /// Validates with ENLU and calls <c>ConfirmPendingPurchase</c> only when the receipt is validated.
        /// A rejected receipt stays pending and is reported; a network error leaves it pending for the
        /// next launch. Never throws: returns the result, or null when validation could not complete.
        /// </summary>
        public static async Task<ReceiptResult?> ValidateAndConfirmAsync(this EnluClient client, IStoreController controller, Product product,
            string? enluProductId = null, CancellationToken cancellationToken = default)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            try
            {
                var result = await client.ValidateAsync(product, enluProductId, cancellationToken);
                if (result.IsValidated) controller.ConfirmPendingPurchase(product);
                else UnityEngine.Debug.LogWarning("[ENLU] Receipt for " + product.definition.id + " was " + result.Status +
                                                  (result.Purchase?.Error != null ? ": " + result.Purchase.Error : "") + ". The purchase stays pending.");
                return result;
            }
            catch (EnluException e)
            {
                UnityEngine.Debug.LogWarning("[ENLU] Could not validate " + product.definition.id + " (" + e.Code + "): " + e.Message +
                                             ". The purchase stays pending and is retried on the next launch.");
                return null;
            }
        }
    }
}
#endif
