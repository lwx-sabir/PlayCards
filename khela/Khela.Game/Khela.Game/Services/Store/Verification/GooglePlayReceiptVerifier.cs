using System;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Database.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Khela.Game.Services.Store.Verification
{
    /// <summary>
    /// Google Play. Input = Unity IAP's unified receipt; the Google payload inside it names the purchaseToken (the unique key),
    /// the product and the package. Cheap pre-checks first (package name, parseability), then the authority: the Play
    /// Developer API — <c>purchases.products.get</c> for one-time products, <c>purchases.subscriptionsv2.get</c> for subscriptions.
    /// Nothing in the receipt is trusted on its own; the API's answer is what the spine grants from.
    /// </summary>
    public sealed class GooglePlayReceiptVerifier : IStoreReceiptVerifier
    {
        private readonly IGooglePlayGateway _google;
        private readonly IOptionsMonitor<StoreOptions> _options;
        private readonly Khela.Game.Services.Redis.IRedisService _redis;
        private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
        private readonly ILogger<GooglePlayReceiptVerifier> _logger;

        public GooglePlayReceiptVerifier(IGooglePlayGateway google, IOptionsMonitor<StoreOptions> options,
            Khela.Game.Services.Redis.IRedisService redis, Microsoft.Extensions.Configuration.IConfiguration config,
            ILogger<GooglePlayReceiptVerifier> logger)
        {
            _google = google; _options = options; _redis = redis; _config = config; _logger = logger;
        }

        /// <summary>Licence-tester purchases accepted? Overlay-aware (Settings ▸ Store) with the appsettings value as the fallback.</summary>
        private Task<bool> AcceptTestPurchasesAsync()
            => StoreSwitches.BoolAsync(_redis, _config, "Store:GooglePlay:AcceptTestPurchases", _options.CurrentValue.GooglePlay.AcceptTestPurchases);

        public StorePlatform Platform => StorePlatform.GooglePlay;
        public bool IsConfigured => _google.IsConfigured;

        public bool TryExtractTransactionKey(RedeemPurchaseRequest req, out string storeTransactionId, out string error)
        {
            storeTransactionId = null; error = null;
            var unified = StoreMath.ParseUnifiedReceipt(req?.Receipt);
            if (unified == null) { error = "Not a Unity unified receipt."; return false; }
            if (!string.Equals(unified.Store, "GooglePlay", StringComparison.OrdinalIgnoreCase)) { error = $"Receipt store '{unified.Store}' is not GooglePlay."; return false; }
            var payload = StoreMath.ParseGooglePayload(unified.Payload);
            if (payload == null) { error = "Google payload has no purchaseToken."; return false; }
            storeTransactionId = payload.PurchaseToken;
            return true;
        }

        public async Task<ReceiptVerification> VerifyAsync(RedeemPurchaseRequest req, StoreProductType productType, CancellationToken ct)
        {
            if (!IsConfigured) return ReceiptVerification.Transient("Google Play verifier is not configured (service account).");
            if (!TryExtractTransactionKey(req, out _, out var err)) return ReceiptVerification.Invalid(err);

            var unified = StoreMath.ParseUnifiedReceipt(req.Receipt);
            var payload = StoreMath.ParseGooglePayload(unified.Payload);
            var o = _options.CurrentValue.GooglePlay;

            if (!string.IsNullOrWhiteSpace(o.PackageName) && !string.IsNullOrWhiteSpace(payload.PackageName)
                && !string.Equals(payload.PackageName, o.PackageName, StringComparison.Ordinal))
                return ReceiptVerification.Invalid($"Receipt package '{payload.PackageName}' is not ours.");

            try
            {
                if (productType == StoreProductType.Subscription)
                {
                    var s = await _google.GetSubscriptionAsync(payload.PurchaseToken, ct);
                    var state = s.SubscriptionState ?? "";
                    // A subscription that has merely ENDED (EXPIRED) or is recoverable (ON_HOLD / PAUSED) is still a real,
                    // paid purchase — it is NOT a revocation, and must stay Valid so the subscription seam runs its
                    // "window ended" branch. Mapping a natural lapse to Invalid+Revoked made every non-renewing Google
                    // subscriber look refunded: the golden window was revoked (expiring their uncollected paid rewards) and,
                    // for any subscription carrying currency lines, the chips were clawed back — for a player the store
                    // never refunded a cent to. Revocation reaches us only through voidedpurchases / RTDN REVOKED (12).
                    //
                    // Order matters: PENDING_PURCHASE_CANCELED ends with "CANCELED", so it must be tested BEFORE the
                    // ordinary states or an abandoned pending purchase would read as a live one.
                    bool purchaseAbandoned = state.EndsWith("PENDING_PURCHASE_CANCELED", StringComparison.OrdinalIgnoreCase);
                    bool pending = !purchaseAbandoned && state.EndsWith("_PENDING", StringComparison.OrdinalIgnoreCase);
                    bool active = !purchaseAbandoned && !pending
                               && (state.EndsWith("ACTIVE", StringComparison.OrdinalIgnoreCase)
                                || state.EndsWith("IN_GRACE_PERIOD", StringComparison.OrdinalIgnoreCase)
                                || state.EndsWith("CANCELED", StringComparison.OrdinalIgnoreCase));   // cancelled = no renewal, still paid until expiry
                    var v = new ReceiptVerification
                    {
                        Outcome = purchaseAbandoned ? VerifyOutcome.Invalid : pending ? VerifyOutcome.PendingPayment : VerifyOutcome.Valid,
                        Reason = active ? null : $"Subscription state {state}.",
                        StoreProductId = s.ProductId ?? payload.ProductId,
                        StoreTransactionId = payload.PurchaseToken,
                        StoreOrderId = s.LatestOrderId ?? payload.OrderId,
                        OriginalTransactionId = StoreMath.FitOrHash(s.LinkedPurchaseToken ?? payload.PurchaseToken, 128),
                        IsTest = s.IsTest,
                        Environment = s.IsTest ? "Sandbox" : "Production",
                        RegionCode = s.RegionCode,
                        ObfuscatedAccountId = s.ObfuscatedExternalAccountId ?? payload.ObfuscatedAccountId,
                        PurchaseTimeUtc = s.StartTimeUtc ?? StoreMath.FromUnixMs(payload.PurchaseTimeMillis),
                        Acknowledged = string.Equals(s.AcknowledgementState, "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED", StringComparison.OrdinalIgnoreCase),
                        IsSubscription = true,
                        SubscriptionStartUtc = s.StartTimeUtc,
                        SubscriptionExpiresUtc = s.ExpiryTimeUtc,
                        AutoRenew = s.AutoRenewing,
                        Revoked = false,   // never inferred from a lifecycle state — only voidedpurchases / RTDN REVOKED mean revoked
                        RawJson = s.RawJson,
                    };
                    if (v.Outcome == VerifyOutcome.Valid && v.IsTest && !await AcceptTestPurchasesAsync())
                        return ReceiptVerification.Invalid("Test purchases are not accepted on this server.");
                    return v;
                }
                else
                {
                    var p = await _google.GetProductPurchaseAsync(payload.ProductId, payload.PurchaseToken, ct);
                    var v = new ReceiptVerification
                    {
                        Outcome = p.PurchaseState == 0 ? VerifyOutcome.Valid : p.PurchaseState == 2 ? VerifyOutcome.PendingPayment : VerifyOutcome.Invalid,
                        Reason = p.PurchaseState == 0 ? null : p.PurchaseState == 2 ? "Payment pending at Google." : "Purchase cancelled at Google.",
                        StoreProductId = p.ProductId ?? payload.ProductId,
                        StoreTransactionId = p.PurchaseToken ?? payload.PurchaseToken,
                        StoreOrderId = p.OrderId ?? payload.OrderId,
                        Quantity = Math.Max(1, p.Quantity),
                        IsTest = p.PurchaseType == 0,
                        Environment = p.PurchaseType == 0 ? "Sandbox" : "Production",
                        RegionCode = p.RegionCode,
                        ObfuscatedAccountId = p.ObfuscatedExternalAccountId ?? payload.ObfuscatedAccountId,
                        PurchaseTimeUtc = p.PurchaseTimeUtc ?? StoreMath.FromUnixMs(payload.PurchaseTimeMillis),
                        Acknowledged = p.AcknowledgementState == 1,
                        Consumed = p.ConsumptionState == 1,
                        Revoked = p.PurchaseState == 1,
                        RawJson = p.RawJson,
                    };
                    if (v.Outcome == VerifyOutcome.Valid && v.IsTest && !await AcceptTestPurchasesAsync())
                        return ReceiptVerification.Invalid("Test purchases are not accepted on this server.");
                    return v;
                }
            }
            catch (GooglePlayNotFoundException ex)
            {
                return ReceiptVerification.Invalid("Google Play does not know this purchase: " + ex.Message);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google Play verification transient failure for token …{Tail}", Tail(payload.PurchaseToken));
                return ReceiptVerification.Transient("Google Play could not be reached: " + ex.Message);
            }
        }

        public async Task<bool> AcknowledgeAsync(StorePurchase purchase, CancellationToken ct)
        {
            if (!IsConfigured || purchase == null) return false;
            try
            {
                if (purchase.ProductType == StoreProductType.Subscription)
                    await _google.AcknowledgeSubscriptionAsync(purchase.StoreProductId, purchase.StoreTransactionId, ct);
                else if (purchase.ProductType == StoreProductType.Consumable)
                    await _google.ConsumeProductAsync(purchase.StoreProductId, purchase.StoreTransactionId, ct);   // consume implies acknowledge
                else
                    await _google.AcknowledgeProductAsync(purchase.StoreProductId, purchase.StoreTransactionId, ct);
                return true;
            }
            catch (GooglePlayNotFoundException)
            {
                return true;   // already consumed/acknowledged (or gone): nothing left to do
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google Play acknowledge failed for purchase {Id}; the reconciler will retry.", purchase.Id);
                return false;
            }
        }

        public Task<ReceiptVerification> RefreshAsync(StorePurchase purchase, CancellationToken ct)
        {
            if (!IsConfigured || purchase == null) return Task.FromResult<ReceiptVerification>(null);
            // Rebuild a minimal request from the stored receipt and re-run the same path.
            var req = new RedeemPurchaseRequest
            {
                Platform = StorePlatform.GooglePlay,
                ProductId = purchase.ProductId,
                StoreProductId = purchase.StoreProductId,
                TransactionId = purchase.StoreOrderId,
                Receipt = purchase.RawReceipt,
            };
            return VerifyAsync(req, purchase.ProductType, ct);
        }

        private static string Tail(string s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 8 ? s : s.Substring(s.Length - 8);
    }
}
