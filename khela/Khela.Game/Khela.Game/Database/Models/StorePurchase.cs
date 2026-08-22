using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Store;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// Lifecycle of a store purchase row. Persisted as <c>int</c> — append only.
    /// Pending → Verified → Granted is the happy path; a row left at Pending/Verified is RE-DRIVEN (crash-safe), never lost.
    /// </summary>
    public enum StorePurchaseStatus
    {
        Pending = 0,
        Verified = 1,
        Granted = 2,
        /// <summary>The store said no (bad signature, cancelled, wrong package/product). Terminal; the receipt stays as evidence.</summary>
        Invalid = 3,
        Refunded = 4,
        Revoked = 5,
        Expired = 6,
    }

    /// <summary>
    /// One real-money store transaction — THE money event of the store, and therefore the row with the full audit.
    ///
    /// <see cref="StoreTransactionId"/> is the store's own id (Google: the purchaseToken — globally unique; the orderId can
    /// be null on test/promo purchases — Apple: transactionId; Fake: <c>fake:{id}</c>) and is UNIQUE per platform: that
    /// index is the idempotency of the whole purchase spine. A replayed receipt, a re-delivered pending order, a retried
    /// request or a crash mid-fulfilment all collide here and are answered from this row, so a purchase can neither pay
    /// twice nor be lost. See docs/IAP_SPEC.md §4.1 / §5.2.
    /// </summary>
    [Table("StorePurchases")]
    [Index(nameof(Platform), nameof(StoreTransactionId), IsUnique = true)]
    [Index(nameof(UserId), nameof(CreatedAt))]
    [Index(nameof(Status), nameof(CreatedAt))]
    [Index(nameof(ProductId), nameof(CreatedAt))]
    [Index(nameof(OperatorId), nameof(CreatedAt))]
    public class StorePurchase
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Which OPERATOR this revenue belongs to (see <see cref="WalletTransaction.OperatorId"/>).</summary>
        [Required, MaxLength(Tenant.MaxLength)] public string OperatorId { get; set; } = Tenant.Default;

        [Required] public Guid UserId { get; set; }

        [Required] public StorePlatform Platform { get; set; }

        /// <summary>OUR catalog id (e.g. <c>chips_01</c>, <c>piggy_t1_full</c>, <c>golden_pass</c>).</summary>
        [Required, MaxLength(64)] public string ProductId { get; set; }

        /// <summary>The platform product id that was actually bought (verified by the store, not the client).</summary>
        [Required, MaxLength(128)] public string StoreProductId { get; set; }

        /// <summary>Google purchaseToken / Apple transactionId / Fake id. Unique with <see cref="Platform"/>.</summary>
        [Required, MaxLength(256)] public string StoreTransactionId { get; set; }

        /// <summary>Google orderId (<c>GPA.…</c>, may be null on test purchases) / Apple transactionId.</summary>
        [MaxLength(128)] public string StoreOrderId { get; set; }

        /// <summary>Apple originalTransactionId; Google subscriptions = a stable hash of the purchase token.</summary>
        [MaxLength(128)] public string OriginalTransactionId { get; set; }

        [Required] public StoreProductType ProductType { get; set; }

        [Required] public StorePurchaseStatus Status { get; set; } = StorePurchaseStatus.Pending;

        /// <summary>Last transient verification error / the reason for <see cref="StorePurchaseStatus.Invalid"/>.</summary>
        [MaxLength(500)] public string LastError { get; set; }

        /// <summary>How many times verification/fulfilment has been attempted (re-drive bookkeeping).</summary>
        public int Attempts { get; set; }

        /// <summary>Google purchaseType 0 (licence tester), Apple Sandbox, Fake. Granted normally; excluded from revenue and spend hooks.</summary>
        public bool IsTest { get; set; }

        /// <summary>"Production" | "Sandbox" | "Fake".</summary>
        [MaxLength(16)] public string Environment { get; set; }

        /// <summary>Google's echo of the obfuscated account id / Apple appAccountToken. A mismatch is a fraud SIGNAL, never a gate.</summary>
        [MaxLength(64)] public string ObfuscatedAccountId { get; set; }

        /// <summary>Store billing region (ISO 3166-1 alpha-2) where the store reports it.</summary>
        [MaxLength(8)] public string RegionCode { get; set; }

        /// <summary>The player's own country at purchase time (ApplicationUser.CountryCode).</summary>
        [MaxLength(8)] public string CountryCode { get; set; }

        /// <summary>Client-reported localized price — informational (the store never tells us the net price at verify time).</summary>
        public long? ClientPriceMicros { get; set; }
        [MaxLength(8)] public string ClientPriceCurrency { get; set; }

        /// <summary>Catalog reference price AT SALE — what spend hooks (VIP/LP) and revenue tiles use.</summary>
        [Precision(18, 4)] public decimal UsdReference { get; set; }

        /// <summary>The product as it was when reserved. Fulfilment reads THIS, so a later catalog edit can't change what a paid purchase pays.</summary>
        public string CatalogSnapshotJson { get; set; }

        /// <summary>The raw unified receipt / JWS, capped by <c>Store:MaxReceiptBytes</c>. Never logged at Info.</summary>
        public string RawReceipt { get; set; }

        /// <summary>Decoded store-API / JWS evidence.</summary>
        public string VerifierJson { get; set; }

        /// <summary>What the ledger actually did — granted lines + effect results.</summary>
        public string FulfilmentJson { get; set; }

        [MaxLength(64)] public string ClientPurchaseId { get; set; }
        [MaxLength(32)] public string ClientVersion { get; set; }

        /// <summary>Google acknowledge/consume confirmed server-side (the 3-day auto-refund backstop).</summary>
        public DateTime? AcknowledgedAt { get; set; }

        /// <summary>For subscriptions: the verified window end.</summary>
        public DateTime? SubscriptionExpiresAt { get; set; }

        public DateTime? RefundedAt { get; set; }
        /// <summary>"google-voided" | "google-rtdn" | "apple-asn" | "admin".</summary>
        [MaxLength(32)] public string RefundSource { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? VerifiedAt { get; set; }
        public DateTime? GrantedAt { get; set; }
        /// <summary>Every leg of fulfilment done. Null after Granted = still owes something → re-drive.</summary>
        public DateTime? CompletedAt { get; set; }
        [Required] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }

    /// <summary>
    /// Every store webhook / poll finding, idempotent on the store's own event id (Pub/Sub messageId, Apple
    /// notificationUUID, <c>voided:{orderId}</c> for the poller). A replayed notification collides here and is ignored.
    /// </summary>
    [Table("StoreEvents")]
    [Index(nameof(Platform), nameof(EventId), IsUnique = true)]
    [Index(nameof(StoreTransactionId))]
    [Index(nameof(ReceivedAt))]
    public class StoreEvent
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();
        [Required] public StorePlatform Platform { get; set; }
        [Required, MaxLength(128)] public string EventId { get; set; }
        [Required, MaxLength(64)] public string EventType { get; set; }
        [MaxLength(256)] public string StoreTransactionId { get; set; }
        public Guid? PurchaseId { get; set; }
        public string RawJson { get; set; }
        [Required] public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ProcessedAt { get; set; }
        [MaxLength(512)] public string Error { get; set; }
    }
}
