using System;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Database.Models;

namespace Khela.Game.Services.Store.Verification
{
    /// <summary>
    /// Unity's Editor FakeStore (<c>{"Store":"fake","TransactionID":"…","Payload":"…"}</c>). Accepts everything, flags it
    /// <c>IsTest</c>, and makes the WHOLE pipeline — catalog → Unity IAP → redeem → ledger → HUD — runnable from the Editor
    /// against a local server with no store account at all.
    ///
    /// It is also the one verifier that would be a free-chip faucet, so it is registered ONLY when the host environment is
    /// Development AND <c>Store:Fake:Enabled</c> (Program.cs) — the environment gate is deliberate: the Redis settings overlay
    /// can flip config fields, it can't flip <c>IHostEnvironment</c>. A receipt whose TransactionID contains <c>FAIL</c> is
    /// answered Invalid (QA hook for the failure path); <c>PENDING</c> answers PendingPayment.
    /// </summary>
    public sealed class FakeStoreReceiptVerifier : IStoreReceiptVerifier
    {
        public const string Prefix = "fake:";
        private readonly bool _enabled;

        public FakeStoreReceiptVerifier(bool enabled) { _enabled = enabled; }

        public StorePlatform Platform => StorePlatform.Fake;
        public bool IsConfigured => _enabled;

        public bool TryExtractTransactionKey(RedeemPurchaseRequest req, out string storeTransactionId, out string error)
        {
            storeTransactionId = null; error = null;
            var unified = StoreMath.ParseUnifiedReceipt(req?.Receipt);
            var id = unified?.TransactionId;
            if (string.IsNullOrWhiteSpace(id)) id = req?.TransactionId;
            if (string.IsNullOrWhiteSpace(id)) { error = "Fake receipt has no transaction id."; return false; }
            storeTransactionId = id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? id.Trim() : Prefix + id.Trim();
            return true;
        }

        public Task<ReceiptVerification> VerifyAsync(RedeemPurchaseRequest req, StoreProductType productType, CancellationToken ct)
        {
            if (!_enabled) return Task.FromResult(ReceiptVerification.Invalid("The fake store is disabled here."));
            if (!TryExtractTransactionKey(req, out var key, out var err)) return Task.FromResult(ReceiptVerification.Invalid(err));

            var unified = StoreMath.ParseUnifiedReceipt(req.Receipt);
            if (unified != null && !string.Equals(unified.Store, "fake", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(ReceiptVerification.Invalid($"Receipt store '{unified.Store}' is not the fake store."));

            if (key.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                return Task.FromResult(ReceiptVerification.Invalid("Fake receipt marked FAIL."));
            if (key.IndexOf("PENDING", StringComparison.OrdinalIgnoreCase) >= 0)
                return Task.FromResult(ReceiptVerification.Pending("Fake receipt marked PENDING."));

            var now = DateTime.UtcNow;
            var storeProductId = string.IsNullOrWhiteSpace(req.StoreProductId) ? req.ProductId : req.StoreProductId;
            return Task.FromResult(new ReceiptVerification
            {
                Outcome = VerifyOutcome.Valid,
                StoreProductId = storeProductId?.Trim(),
                StoreTransactionId = key,
                StoreOrderId = key,
                OriginalTransactionId = key,
                IsTest = true,
                Environment = "Fake",
                PurchaseTimeUtc = now,
                Acknowledged = true,
                Consumed = true,
                PriceMicros = req.ClientPriceMicros,
                PriceCurrency = req.ClientPriceCurrency,
                IsSubscription = productType == StoreProductType.Subscription,
                SubscriptionStartUtc = productType == StoreProductType.Subscription ? now : (DateTime?)null,
                SubscriptionExpiresUtc = productType == StoreProductType.Subscription ? now.AddDays(30) : (DateTime?)null,
                AutoRenew = false,
                RawJson = "{\"store\":\"fake\",\"transactionId\":\"" + key + "\"}",
            });
        }

        public Task<bool> AcknowledgeAsync(StorePurchase purchase, CancellationToken ct) => Task.FromResult(true);

        public Task<ReceiptVerification> RefreshAsync(StorePurchase purchase, CancellationToken ct) => Task.FromResult<ReceiptVerification>(null);
    }
}
