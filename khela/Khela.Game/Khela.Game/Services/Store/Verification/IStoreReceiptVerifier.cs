using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Database.Models;

namespace Khela.Game.Services.Store.Verification
{
    /// <summary>What the store said about a receipt.</summary>
    public enum VerifyOutcome
    {
        /// <summary>Paid and deliverable.</summary>
        Valid = 0,
        /// <summary>The store accepted the order but payment is not complete yet (Google deferred/pending payment). Keep the row Pending; poll/RTDN finishes it.</summary>
        PendingPayment = 1,
        /// <summary>Definitively not a deliverable purchase (bad signature, cancelled, wrong package/product, revoked). Terminal.</summary>
        Invalid = 2,
        /// <summary>Could not ask the store right now (network, 5xx, credentials). Retry later; nothing is concluded.</summary>
        Transient = 3,
    }

    /// <summary>The verifier's answer — the ONLY facts the spine acts on. Everything the client sent is a hint; this is the truth.</summary>
    public sealed class ReceiptVerification
    {
        public VerifyOutcome Outcome { get; set; }
        public string Reason { get; set; }

        public string StoreProductId { get; set; }
        /// <summary>Google purchaseToken / Apple transactionId / Fake id — the unique key.</summary>
        public string StoreTransactionId { get; set; }
        public string StoreOrderId { get; set; }
        public string OriginalTransactionId { get; set; }
        public int Quantity { get; set; } = 1;
        public bool IsTest { get; set; }
        /// <summary>"Production" | "Sandbox" | "Fake".</summary>
        public string Environment { get; set; }
        public string RegionCode { get; set; }
        public string ObfuscatedAccountId { get; set; }
        public DateTime? PurchaseTimeUtc { get; set; }
        /// <summary>Google: acknowledgementState == 1 (one-time) / "ACKNOWLEDGED" (subs). Null where the store has no such notion.</summary>
        public bool? Acknowledged { get; set; }
        public bool? Consumed { get; set; }
        /// <summary>Client-reported price echoed by the store where available (Apple JWS carries price + currency).</summary>
        public long? PriceMicros { get; set; }
        public string PriceCurrency { get; set; }

        public bool IsSubscription { get; set; }
        public DateTime? SubscriptionStartUtc { get; set; }
        public DateTime? SubscriptionExpiresUtc { get; set; }
        public bool AutoRenew { get; set; }
        /// <summary>The store has revoked/refunded this transaction (Apple revocationDate, Google CANCELED/voided).</summary>
        public bool Revoked { get; set; }

        /// <summary>Decoded evidence (store API response / JWS payload) — stored on the purchase row, never logged at Info.</summary>
        public string RawJson { get; set; }

        public static ReceiptVerification Invalid(string reason) => new ReceiptVerification { Outcome = VerifyOutcome.Invalid, Reason = reason };
        public static ReceiptVerification Transient(string reason) => new ReceiptVerification { Outcome = VerifyOutcome.Transient, Reason = reason };
        public static ReceiptVerification Pending(string reason) => new ReceiptVerification { Outcome = VerifyOutcome.PendingPayment, Reason = reason };
    }

    /// <summary>
    /// One store vendor's receipt verification (docs/IAP_SPEC.md §6). One implementation per <see cref="StorePlatform"/>;
    /// adding a store = adding a class here + its config. Rules every implementation keeps:
    /// <list type="bullet">
    /// <item><see cref="VerifyAsync"/> NEVER throws for a bad receipt — it answers <see cref="VerifyOutcome.Invalid"/> with a reason;
    /// it answers <see cref="VerifyOutcome.Transient"/> when the store could not be asked.</item>
    /// <item><see cref="TryExtractTransactionKey"/> is a pure parse (no network): the spine needs the store's transaction id
    /// BEFORE verification to reserve the unique row.</item>
    /// <item>Nothing the client sent is trusted on its own — the store's answer names the product.</item>
    /// </list>
    /// </summary>
    public interface IStoreReceiptVerifier
    {
        StorePlatform Platform { get; }

        /// <summary>Credentials/config loaded — a platform whose verifier is not configured answers PlatformDisabled.</summary>
        bool IsConfigured { get; }

        /// <summary>Pure parse: the store transaction id this receipt is about (Google purchaseToken, Apple transactionId, Fake id).</summary>
        bool TryExtractTransactionKey(RedeemPurchaseRequest req, out string storeTransactionId, out string error);

        /// <summary>Ask the store. <paramref name="productType"/> tells a vendor which API to call (one-time vs subscription).</summary>
        Task<ReceiptVerification> VerifyAsync(RedeemPurchaseRequest req, StoreProductType productType, CancellationToken ct);

        /// <summary>Acknowledge/consume server-side where the store requires it (Google: un-acknowledged purchases auto-refund after
        /// 3 days). Returns true when acknowledged or not applicable; false when it could not be done now.</summary>
        Task<bool> AcknowledgeAsync(StorePurchase purchase, CancellationToken ct);

        /// <summary>Re-check a known purchase (refunds, subscription renewal/expiry). Null when the vendor can't (no API credentials).</summary>
        Task<ReceiptVerification> RefreshAsync(StorePurchase purchase, CancellationToken ct);
    }

    /// <summary>Platform → verifier. Built from DI; only verifiers registered for this environment are resolvable.</summary>
    public sealed class StoreVerifierRegistry
    {
        private readonly Dictionary<StorePlatform, IStoreReceiptVerifier> _byPlatform = new Dictionary<StorePlatform, IStoreReceiptVerifier>();

        public StoreVerifierRegistry(IEnumerable<IStoreReceiptVerifier> verifiers)
        {
            foreach (var v in verifiers ?? Array.Empty<IStoreReceiptVerifier>())
                _byPlatform[v.Platform] = v;   // last registration wins
        }

        public IStoreReceiptVerifier Resolve(StorePlatform platform)
            => _byPlatform.TryGetValue(platform, out var v) ? v : null;

        public IReadOnlyCollection<StorePlatform> Registered => _byPlatform.Keys.ToList();
    }
}
