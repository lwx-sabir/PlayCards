using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Store.Verification;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Store
{
    // ------------------------------------------------------------------ Google Play RTDN (Pub/Sub push) shapes

    /// <summary>Google Play Real-time Developer Notification (the decoded <c>message.data</c> of a Pub/Sub push).</summary>
    public sealed class GoogleDeveloperNotification
    {
        [JsonPropertyName("version")] public string Version { get; set; }
        [JsonPropertyName("packageName")] public string PackageName { get; set; }
        [JsonPropertyName("eventTimeMillis")] public long? EventTimeMillis { get; set; }
        [JsonPropertyName("oneTimeProductNotification")] public GoogleOneTimeProductNotification OneTimeProduct { get; set; }
        [JsonPropertyName("subscriptionNotification")] public GoogleSubscriptionNotification Subscription { get; set; }
        [JsonPropertyName("voidedPurchaseNotification")] public GoogleVoidedPurchaseNotification Voided { get; set; }
        [JsonPropertyName("testNotification")] public GoogleTestNotification Test { get; set; }
    }

    public sealed class GoogleOneTimeProductNotification
    {
        [JsonPropertyName("version")] public string Version { get; set; }
        /// <summary>1 = ONE_TIME_PRODUCT_PURCHASED, 2 = ONE_TIME_PRODUCT_CANCELED.</summary>
        [JsonPropertyName("notificationType")] public int NotificationType { get; set; }
        [JsonPropertyName("purchaseToken")] public string PurchaseToken { get; set; }
        [JsonPropertyName("sku")] public string Sku { get; set; }
    }

    public sealed class GoogleSubscriptionNotification
    {
        [JsonPropertyName("version")] public string Version { get; set; }
        /// <summary>1 RECOVERED · 2 RENEWED · 3 CANCELED · 4 PURCHASED · 5 ON_HOLD · 6 IN_GRACE_PERIOD · 7 RESTARTED · 8 PRICE_CHANGE_CONFIRMED ·
        /// 9 DEFERRED · 10 PAUSED · 11 PAUSE_SCHEDULE_CHANGED · 12 REVOKED · 13 EXPIRED · 20 PENDING_PURCHASE_CANCELED.</summary>
        [JsonPropertyName("notificationType")] public int NotificationType { get; set; }
        [JsonPropertyName("purchaseToken")] public string PurchaseToken { get; set; }
        [JsonPropertyName("subscriptionId")] public string SubscriptionId { get; set; }
    }

    public sealed class GoogleVoidedPurchaseNotification
    {
        [JsonPropertyName("purchaseToken")] public string PurchaseToken { get; set; }
        [JsonPropertyName("orderId")] public string OrderId { get; set; }
        /// <summary>1 = subscription, 2 = one-time product.</summary>
        [JsonPropertyName("productType")] public int ProductType { get; set; }
        /// <summary>1 = full refund, 2 = quantity-based partial refund.</summary>
        [JsonPropertyName("refundType")] public int RefundType { get; set; }
    }

    public sealed class GoogleTestNotification
    {
        [JsonPropertyName("version")] public string Version { get; set; }
    }

    /// <summary>An Apple App Store Server Notification v2, already verified and flattened by the Apple verifier.</summary>
    public sealed class AppleNotification
    {
        /// <summary>SUBSCRIBED · DID_RENEW · DID_CHANGE_RENEWAL_STATUS · EXPIRED · GRACE_PERIOD_EXPIRED · REFUND · REFUND_DECLINED · REFUND_REVERSED · REVOKE · CONSUMPTION_REQUEST · TEST …</summary>
        public string NotificationType { get; set; }
        public string Subtype { get; set; }
        public string NotificationUuid { get; set; }
        public string Environment { get; set; }
        public string BundleId { get; set; }
        public string TransactionId { get; set; }
        public string OriginalTransactionId { get; set; }
        public string ProductId { get; set; }
        public string Type { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public DateTime? RevocationUtc { get; set; }
        public bool? AutoRenew { get; set; }
        public string RawJson { get; set; }
    }

    public enum WebhookOutcome { Ignored = 0, Duplicate = 1, Applied = 2, NoMatch = 3, Error = 4 }

    /// <summary>
    /// Store webhooks (docs/IAP_SPEC.md §5.5): Google Play RTDN and Apple Server Notifications v2. Every notification is
    /// recorded in <c>StoreEvents</c> idempotently on the store's own event id (a replay is a no-op), then applied through the
    /// purchase service's existing seams — refund policy for refunds/voids, the subscription seam for renew/expire/revoke —
    /// so a webhook can never do something the reconciler or an admin couldn't. Unknown purchases are recorded as NoMatch
    /// (the client's next FetchPurchases may still redeem them); nothing is ever granted from a webhook alone.
    /// </summary>
    public interface IStoreWebhookService
    {
        /// <summary>Parse a Pub/Sub push body. Returns null when it isn't one.</summary>
        (string MessageId, string RawData, GoogleDeveloperNotification Notification)? ParseGooglePush(string body);

        Task<WebhookOutcome> HandleGoogleAsync(string messageId, string rawJson, GoogleDeveloperNotification notification, CancellationToken ct = default);

        Task<WebhookOutcome> HandleAppleAsync(AppleNotification notification, CancellationToken ct = default);
    }

    public sealed class StoreWebhookService : IStoreWebhookService
    {
        private readonly AppDbContext _db;
        private readonly IStorePurchaseService _purchases;
        private readonly StoreVerifierRegistry _verifiers;
        private readonly ILogger<StoreWebhookService> _logger;

        public StoreWebhookService(AppDbContext db, IStorePurchaseService purchases, StoreVerifierRegistry verifiers, ILogger<StoreWebhookService> logger)
        {
            _db = db; _purchases = purchases; _verifiers = verifiers; _logger = logger;
        }

        private static readonly JsonSerializerOptions Lenient = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public (string MessageId, string RawData, GoogleDeveloperNotification Notification)? ParseGooglePush(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
                var messageId = message.TryGetProperty("messageId", out var mid) ? mid.GetString() : (message.TryGetProperty("message_id", out var mid2) ? mid2.GetString() : null);
                if (!message.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) return null;
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()));
                var notification = JsonSerializer.Deserialize<GoogleDeveloperNotification>(raw, Lenient);
                if (notification == null) return null;
                return (messageId ?? StoreMath.Sha256Hex(raw).Substring(0, 32), raw, notification);
            }
            catch { return null; }
        }

        public async Task<WebhookOutcome> HandleGoogleAsync(string messageId, string rawJson, GoogleDeveloperNotification n, CancellationToken ct = default)
        {
            if (n == null) return WebhookOutcome.Ignored;
            string type; string token = null;
            if (n.Test != null) type = "test";
            else if (n.Voided != null) { type = "voided"; token = n.Voided.PurchaseToken; }
            else if (n.OneTimeProduct != null) { type = "onetime:" + n.OneTimeProduct.NotificationType; token = n.OneTimeProduct.PurchaseToken; }
            else if (n.Subscription != null) { type = "subscription:" + n.Subscription.NotificationType; token = n.Subscription.PurchaseToken; }
            else type = "unknown";

            var eventId = StoreMath.FitOrHash(string.IsNullOrWhiteSpace(messageId) ? "rtdn:" + StoreMath.Sha256Hex(rawJson ?? "") : messageId, 128);
            var row = await _purchases.FindAsync(StorePlatform.GooglePlay, token, ct);
            var ev = await RecordAsync(StorePlatform.GooglePlay, eventId, type, token, row?.Id, rawJson, ct);
            if (ev == null) return WebhookOutcome.Duplicate;

            try
            {
                WebhookOutcome outcome;
                if (n.Test != null || type == "unknown") outcome = WebhookOutcome.Ignored;
                else if (row == null) outcome = WebhookOutcome.NoMatch;
                else if (n.Voided != null)
                {
                    // refundType 2 = QUANTITY-BASED partial refund: some units came back, not the purchase. Never claw back the whole grant for it.
                    await _purchases.MarkRefundedAsync(row.Id, "google-rtdn", $"voided (type {n.Voided.ProductType}, refund {n.Voided.RefundType})", partial: n.Voided.RefundType == 2, ct: ct);
                    outcome = WebhookOutcome.Applied;
                }
                else if (n.OneTimeProduct != null)
                {
                    if (n.OneTimeProduct.NotificationType == 2) { await _purchases.MarkRefundedAsync(row.Id, "google-rtdn", "one-time product cancelled", ct: ct); outcome = WebhookOutcome.Applied; }
                    else outcome = WebhookOutcome.Ignored;   // PURCHASED: the client redeems; a pending payment completing is picked up by redeem/re-drive
                }
                else
                {
                    outcome = await ApplyGoogleSubscriptionAsync(row, n.Subscription.NotificationType, ct);
                }
                await CompleteAsync(ev, outcome == WebhookOutcome.Error ? "apply failed" : null, ct);
                return outcome;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google RTDN apply failed for {EventId}", eventId);
                await CompleteAsync(ev, ex.Message, ct);
                return WebhookOutcome.Error;
            }
        }

        private async Task<WebhookOutcome> ApplyGoogleSubscriptionAsync(StorePurchase row, int notificationType, CancellationToken ct)
        {
            switch (notificationType)
            {
                case 12:   // REVOKED
                    await _purchases.MarkRefundedAsync(row.Id, "google-rtdn", "subscription revoked", ct: ct);
                    return WebhookOutcome.Applied;
                case 20:   // PENDING_PURCHASE_CANCELED
                    if (row.Status == StorePurchaseStatus.Pending) { row.Status = StorePurchaseStatus.Invalid; row.LastError = "pending purchase cancelled (RTDN)"; row.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct); return WebhookOutcome.Applied; }
                    return WebhookOutcome.Ignored;
                case 1: case 2: case 3: case 4: case 5: case 6: case 7: case 13:
                    // RECOVERED / RENEWED / CANCELED(still paid till expiry) / PURCHASED / ON_HOLD / GRACE / RESTARTED / EXPIRED:
                    // ask Google for the truth and apply it through the shared seam (extend / expire).
                    var verifier = _verifiers.Resolve(StorePlatform.GooglePlay);
                    if (verifier == null || !verifier.IsConfigured) return WebhookOutcome.Ignored;
                    var fresh = await verifier.RefreshAsync(row, ct);
                    if (fresh == null) return WebhookOutcome.Ignored;
                    var result = await _purchases.ApplySubscriptionUpdateAsync(row, fresh, "google-rtdn", ct);
                    return result == SubscriptionUpdate.NoChange ? WebhookOutcome.Ignored : WebhookOutcome.Applied;
                default:
                    return WebhookOutcome.Ignored;   // price change / deferred / paused — nothing to do for the entitlement itself
            }
        }

        public async Task<WebhookOutcome> HandleAppleAsync(AppleNotification n, CancellationToken ct = default)
        {
            if (n == null) return WebhookOutcome.Ignored;
            var type = (n.NotificationType ?? "").Trim().ToUpperInvariant() + (string.IsNullOrWhiteSpace(n.Subtype) ? "" : "/" + n.Subtype.Trim().ToUpperInvariant());
            var eventId = StoreMath.FitOrHash(string.IsNullOrWhiteSpace(n.NotificationUuid) ? "asn:" + StoreMath.Sha256Hex(n.RawJson ?? type + n.TransactionId) : n.NotificationUuid, 128);

            var row = await _purchases.FindAsync(StorePlatform.AppStore, n.TransactionId, ct)
                      ?? await _purchases.FindByOriginalTransactionAsync(StorePlatform.AppStore, n.OriginalTransactionId, ct);
            var ev = await RecordAsync(StorePlatform.AppStore, eventId, type, n.TransactionId ?? n.OriginalTransactionId, row?.Id, n.RawJson, ct);
            if (ev == null) return WebhookOutcome.Duplicate;

            try
            {
                WebhookOutcome outcome;
                var t = (n.NotificationType ?? "").Trim().ToUpperInvariant();
                if (t == "TEST" || t == "CONSUMPTION_REQUEST" || t == "REFUND_DECLINED" || t == "PRICE_INCREASE" || t == "RENEWAL_EXTENDED" || t == "RENEWAL_EXTENSION") outcome = WebhookOutcome.Ignored;
                else if (row == null) outcome = WebhookOutcome.NoMatch;
                else if (t == "REFUND" || t == "REVOKE")
                {
                    await _purchases.MarkRefundedAsync(row.Id, "apple-asn", t.ToLowerInvariant() + (n.RevocationUtc.HasValue ? " at " + n.RevocationUtc.Value.ToString("u") : ""), ct: ct);
                    outcome = WebhookOutcome.Applied;
                }
                else if (t == "REFUND_REVERSED")
                {
                    // Apple reversed a refund: the purchase stands again. We do not re-grant automatically (the chips may have been
                    // rolled back and re-granting needs a human look) — flag the row for the admin.
                    row.LastError = "Apple reversed the refund — review and re-grant manually if the chips were rolled back.";
                    row.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct);
                    outcome = WebhookOutcome.Applied;
                }
                else if (row.ProductType == StoreProductType.Subscription)
                {
                    // SUBSCRIBED / DID_RENEW / DID_CHANGE_RENEWAL_STATUS / EXPIRED / GRACE_PERIOD_EXPIRED / DID_FAIL_TO_RENEW …
                    var fresh = new ReceiptVerification
                    {
                        Outcome = VerifyOutcome.Valid,
                        StoreProductId = n.ProductId ?? row.StoreProductId,
                        StoreTransactionId = n.TransactionId ?? row.StoreTransactionId,
                        StoreOrderId = n.TransactionId ?? row.StoreOrderId,
                        OriginalTransactionId = n.OriginalTransactionId ?? row.OriginalTransactionId,
                        IsSubscription = true,
                        SubscriptionExpiresUtc = n.ExpiresUtc ?? row.SubscriptionExpiresAt,
                        AutoRenew = n.AutoRenew ?? !(t == "EXPIRED" || t == "GRACE_PERIOD_EXPIRED"),
                        Revoked = n.RevocationUtc.HasValue,
                        RawJson = n.RawJson,
                    };
                    if (t == "EXPIRED" || t == "GRACE_PERIOD_EXPIRED") { fresh.AutoRenew = false; fresh.SubscriptionExpiresUtc ??= DateTime.UtcNow; }
                    var result = await _purchases.ApplySubscriptionUpdateAsync(row, fresh, "apple-asn", ct);
                    outcome = result == SubscriptionUpdate.NoChange ? WebhookOutcome.Ignored : WebhookOutcome.Applied;
                }
                else outcome = WebhookOutcome.Ignored;
                await CompleteAsync(ev, null, ct);
                return outcome;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Apple notification apply failed for {EventId}", eventId);
                await CompleteAsync(ev, ex.Message, ct);
                return WebhookOutcome.Error;
            }
        }

        // ---- the event row (idempotency) ----

        private async Task<StoreEvent> RecordAsync(StorePlatform platform, string eventId, string type, string storeTransactionId, Guid? purchaseId, string rawJson, CancellationToken ct)
        {
            if (await _db.StoreEvents.AsNoTracking().AnyAsync(e => e.Platform == platform && e.EventId == eventId, ct)) return null;
            var ev = new StoreEvent
            {
                Platform = platform, EventId = eventId, EventType = Clamp(type, 64),
                StoreTransactionId = Clamp(storeTransactionId, 256), PurchaseId = purchaseId,
                RawJson = rawJson, ReceivedAt = DateTime.UtcNow,
                Error = purchaseId == null ? "no matching purchase row" : null,
            };
            _db.StoreEvents.Add(ev);
            try { await _db.SaveChangesAsync(ct); return ev; }
            catch (DbUpdateException) { _db.ChangeTracker.Clear(); return null; }   // lost the race to a concurrent delivery of the same event
        }

        private async Task CompleteAsync(StoreEvent ev, string error, CancellationToken ct)
        {
            try
            {
                ev.ProcessedAt = DateTime.UtcNow;
                if (error != null) ev.Error = Clamp(error, 512);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "store event completion save failed"); }
        }

        private static string Clamp(string s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));
    }
}
