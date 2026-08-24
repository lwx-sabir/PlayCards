using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Khela.Common.Store;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Rewards;
using Khela.Game.Services.Store;
using Khela.Game.Services.Store.Grants;
using Khela.Game.Services.Store.Verification;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Store webhooks (docs/IAP_SPEC.md §5.5): a Pub/Sub push parses into a DeveloperNotification; every notification is
    /// recorded idempotently (a replay is a Duplicate and changes nothing); a refund/void/revoke applies the refund policy
    /// through the purchase service; unknown purchases are recorded as NoMatch and grant nothing.
    /// </summary>
    [Collection("khela-db")]
    public class StoreWebhookTests : IClassFixture<KhelaDbFixture>
    {
        private readonly KhelaDbFixture _fx;
        public StoreWebhookTests(KhelaDbFixture fx) { _fx = fx; }

        private sealed class Stack : IDisposable
        {
            public AppDbContext Db;
            public WalletService Wallet;
            public StorePurchaseService Purchases;
            public StoreWebhookService Webhooks;
            public void Dispose() => Db.Dispose();
        }

        private Stack NewStack()
        {
            var db = _fx.NewContext();
            var wallet = new WalletService(db, NullLogger<WalletService>.Instance);
            var grants = new RewardGrantService(new IRewardGranter[] { new CurrencyGranter(wallet, NullLogger<CurrencyGranter>.Instance) }, NullLogger<RewardGrantService>.Instance);
            var catalog = new StoreCatalogService(db, new NoRedis(), null, NullLogger<StoreCatalogService>.Instance, new TestObjectStorage());
            var registry = new StoreVerifierRegistry(new IStoreReceiptVerifier[] { new FakeStoreReceiptVerifier(true) });
            var purchases = new StorePurchaseService(db, catalog, registry, Array.Empty<IStoreGrantHandler>(), wallet, grants,
                null, null, null, null, new NoRedis(), null, new StaticOptionsMonitor<StoreOptions>(new StoreOptions()), NullLogger<StorePurchaseService>.Instance);
            var webhooks = new StoreWebhookService(db, purchases, registry, NullLogger<StoreWebhookService>.Instance);
            return new Stack { Db = db, Wallet = wallet, Purchases = purchases, Webhooks = webhooks };
        }

        private static string PubSubPush(string messageId, object developerNotification)
        {
            var raw = System.Text.Json.JsonSerializer.Serialize(developerNotification);
            var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            return "{\"message\":{\"data\":\"" + data + "\",\"messageId\":\"" + messageId + "\",\"publishTime\":\"2026-08-22T10:00:00Z\"},\"subscription\":\"projects/x/subscriptions/y\"}";
        }

        /// <summary>A GRANTED Google purchase row with one credited chip line — what a real Play purchase leaves behind.</summary>
        private async Task<StorePurchase> SeedGooglePurchaseAsync(Stack s, Guid user, string token, decimal chips = 5_000_000m)
        {
            var id = Guid.NewGuid();
            var key = StoreMath.LineKey(id, 0);
            var txn = await s.Wallet.CreditAsync(user.ToString(), CurrencyType.Chips, chips, TransactionType.PaidPurchase, key, new WalletContext { Description = "Store chips_01", ExternalRef = "GooglePlay:" + id.ToString("N") });
            var fulfilment = new StoreFulfilment();
            fulfilment.Lines.Add(new StoreFulfilledLine { Kind = (int)RewardKind.Currency, Id = "Chips", Amount = chips, CorrelationId = key, Balance = txn.BalanceAfter ?? chips });
            fulfilment.CompletedAtUtc = DateTime.UtcNow;
            var row = new StorePurchase
            {
                Id = id, UserId = user, Platform = StorePlatform.GooglePlay, ProductId = "chips_01", StoreProductId = "chips_01",
                StoreTransactionId = token, StoreOrderId = "GPA.1234-5678-9012-" + token.Substring(token.Length - 5), ProductType = StoreProductType.Consumable,
                Status = StorePurchaseStatus.Granted, UsdReference = 1.99m, CatalogSnapshotJson = System.Text.Json.JsonSerializer.Serialize(StoreCatalog.Defaults().Find("chips_01"), StoreCatalog.JsonOptions),
                FulfilmentJson = System.Text.Json.JsonSerializer.Serialize(fulfilment, StoreCatalog.JsonOptions),
                VerifiedAt = DateTime.UtcNow, GrantedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            };
            s.Db.StorePurchases.Add(row);
            await s.Db.SaveChangesAsync();
            return row;
        }

        [Fact]
        public void ParseGooglePush_DecodesTheDeveloperNotification()
        {
            using var s = NewStack();
            var body = PubSubPush("m-1", new { version = "1.0", packageName = "com.casuallabinteractive.khela", eventTimeMillis = 1756000000000, oneTimeProductNotification = new { version = "1.0", notificationType = 2, purchaseToken = "tok-123", sku = "chips_01" } });
            var parsed = s.Webhooks.ParseGooglePush(body);
            Assert.NotNull(parsed);
            Assert.Equal("m-1", parsed.Value.MessageId);
            Assert.Equal("com.casuallabinteractive.khela", parsed.Value.Notification.PackageName);
            Assert.Equal(2, parsed.Value.Notification.OneTimeProduct.NotificationType);
            Assert.Equal("tok-123", parsed.Value.Notification.OneTimeProduct.PurchaseToken);
            Assert.Null(parsed.Value.Notification.Subscription);
            Assert.Null(s.Webhooks.ParseGooglePush("not json"));
            Assert.Null(s.Webhooks.ParseGooglePush("{\"message\":{}}"));
        }

        [Fact]
        public async Task GoogleOneTimeCancelled_RefundsTheMatchingPurchase_Once()
        {
            var user = Guid.NewGuid();
            var token = "tok-" + Guid.NewGuid().ToString("N");
            using var s = NewStack();
            var row = await SeedGooglePurchaseAsync(s, user, token);
            Assert.Equal(5_000_000m, await s.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));

            var n = new GoogleDeveloperNotification { PackageName = "com.casuallabinteractive.khela", OneTimeProduct = new GoogleOneTimeProductNotification { NotificationType = 2, PurchaseToken = token, Sku = "chips_01" } };
            var first = await s.Webhooks.HandleGoogleAsync("msg-" + token, "{}", n);
            Assert.Equal(WebhookOutcome.Applied, first);

            // Rollback policy (default): the credit was reversed, the row is Refunded, the event recorded.
            Assert.Equal(0m, await s.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
            var after = await s.Db.StorePurchases.AsNoTracking().SingleAsync(p => p.Id == row.Id);
            Assert.Equal(StorePurchaseStatus.Refunded, after.Status);
            Assert.Equal("google-rtdn", after.RefundSource);
            var ev = await s.Db.StoreEvents.AsNoTracking().SingleAsync(e => e.Platform == StorePlatform.GooglePlay && e.EventId == "msg-" + token);
            Assert.Equal(row.Id, ev.PurchaseId);
            Assert.NotNull(ev.ProcessedAt);

            // Replay of the same Pub/Sub message → Duplicate, nothing changes.
            using var s2 = NewStack();
            var again = await s2.Webhooks.HandleGoogleAsync("msg-" + token, "{}", n);
            Assert.Equal(WebhookOutcome.Duplicate, again);
            Assert.Equal(0m, await s2.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
            Assert.Equal(2, (await s2.Db.WalletTransactions.AsNoTracking().CountAsync(t => t.Description == "Store chips_01" || t.Description.StartsWith("Store refund"))));
        }

        [Fact]
        public async Task GoogleVoided_ForUnknownToken_IsRecordedAsNoMatch_AndGrantsNothing()
        {
            using var s = NewStack();
            var n = new GoogleDeveloperNotification { Voided = new GoogleVoidedPurchaseNotification { PurchaseToken = "never-seen-" + Guid.NewGuid().ToString("N"), OrderId = "GPA.0", ProductType = 2, RefundType = 1 } };
            var id = "msg-" + Guid.NewGuid().ToString("N");
            Assert.Equal(WebhookOutcome.NoMatch, await s.Webhooks.HandleGoogleAsync(id, "{}", n));
            var ev = await s.Db.StoreEvents.AsNoTracking().SingleAsync(e => e.EventId == id);
            Assert.Null(ev.PurchaseId);
            Assert.Contains("no matching", ev.Error);
        }

        [Fact]
        public async Task GoogleTestNotification_IsIgnored_ButRecorded()
        {
            using var s = NewStack();
            var id = "msg-" + Guid.NewGuid().ToString("N");
            Assert.Equal(WebhookOutcome.Ignored, await s.Webhooks.HandleGoogleAsync(id, "{}", new GoogleDeveloperNotification { Test = new GoogleTestNotification { Version = "1.0" } }));
            Assert.True(await s.Db.StoreEvents.AsNoTracking().AnyAsync(e => e.EventId == id && e.EventType == "test"));
        }

        [Fact]
        public async Task AppleRefund_RefundsTheMatchingPurchase_AndReplayIsDuplicate()
        {
            var user = Guid.NewGuid();
            using var s = NewStack();
            // an Apple row: transaction id is the key
            var id = Guid.NewGuid();
            var key = StoreMath.LineKey(id, 0);
            var txn = await s.Wallet.CreditAsync(user.ToString(), CurrencyType.Kash, 100m, TransactionType.PaidPurchase, key, new WalletContext { Description = "Store kash_01", ExternalRef = "AppStore:2000000999" });
            var fulfilment = new StoreFulfilment { CompletedAtUtc = DateTime.UtcNow };
            fulfilment.Lines.Add(new StoreFulfilledLine { Kind = (int)RewardKind.Currency, Id = "Kash", Amount = 100m, CorrelationId = key, Balance = txn.BalanceAfter ?? 100m });
            var txnId = "2000000" + new Random().Next(100000, 999999);
            s.Db.StorePurchases.Add(new StorePurchase
            {
                Id = id, UserId = user, Platform = StorePlatform.AppStore, ProductId = "kash_01", StoreProductId = "kash_01", StoreTransactionId = txnId, StoreOrderId = txnId,
                OriginalTransactionId = txnId, ProductType = StoreProductType.Consumable, Status = StorePurchaseStatus.Granted, UsdReference = 1.99m,
                FulfilmentJson = System.Text.Json.JsonSerializer.Serialize(fulfilment, StoreCatalog.JsonOptions), VerifiedAt = DateTime.UtcNow, GrantedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            });
            await s.Db.SaveChangesAsync();

            var n = new AppleNotification { NotificationType = "REFUND", NotificationUuid = Guid.NewGuid().ToString("D"), Environment = "Sandbox", TransactionId = txnId, OriginalTransactionId = txnId, ProductId = "kash_01", RevocationUtc = DateTime.UtcNow, RawJson = "{}" };
            Assert.Equal(WebhookOutcome.Applied, await s.Webhooks.HandleAppleAsync(n));
            Assert.Equal(0m, await s.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Kash));
            Assert.Equal(StorePurchaseStatus.Refunded, (await s.Db.StorePurchases.AsNoTracking().SingleAsync(p => p.Id == id)).Status);

            using var s2 = NewStack();
            Assert.Equal(WebhookOutcome.Duplicate, await s2.Webhooks.HandleAppleAsync(n));
            Assert.Equal(WebhookOutcome.Ignored, await s2.Webhooks.HandleAppleAsync(new AppleNotification { NotificationType = "TEST", NotificationUuid = Guid.NewGuid().ToString("D"), RawJson = "{}" }));
        }

        [Fact]
        public async Task SubscriptionUpdate_ExtendsExpiresExpiredAndRevokes()
        {
            var user = Guid.NewGuid();
            using var s = NewStack();
            var txnId = "2000001" + new Random().Next(100000, 999999);
            var start = DateTime.UtcNow.AddDays(-30);
            var row = new StorePurchase
            {
                UserId = user, Platform = StorePlatform.AppStore, ProductId = "golden_pass", StoreProductId = "golden_pass", StoreTransactionId = txnId, StoreOrderId = txnId,
                OriginalTransactionId = txnId, ProductType = StoreProductType.Subscription, Status = StorePurchaseStatus.Granted, UsdReference = 4.99m,
                SubscriptionExpiresAt = DateTime.UtcNow.AddHours(-1), CatalogSnapshotJson = System.Text.Json.JsonSerializer.Serialize(StoreCatalog.Defaults().Find("golden_pass"), StoreCatalog.JsonOptions),
                FulfilmentJson = System.Text.Json.JsonSerializer.Serialize(new StoreFulfilment { CompletedAtUtc = DateTime.UtcNow }, StoreCatalog.JsonOptions),
                VerifiedAt = start, GrantedAt = start, CompletedAt = start,
            };
            s.Db.StorePurchases.Add(row);
            await s.Db.SaveChangesAsync();

            // Renewed: a later expiry → Extended (no IPassService in this stack → the window is recorded on the row only).
            var renewed = new ReceiptVerification { Outcome = VerifyOutcome.Valid, IsSubscription = true, SubscriptionExpiresUtc = DateTime.UtcNow.AddDays(29), AutoRenew = true, StoreOrderId = txnId + ".1" };
            Assert.Equal(SubscriptionUpdate.Extended, await s.Purchases.ApplySubscriptionUpdateAsync(row, renewed, "test"));
            Assert.True(row.SubscriptionExpiresAt > DateTime.UtcNow.AddDays(28));
            // Same answer again → NoChange (idempotent on the window end).
            Assert.Equal(SubscriptionUpdate.NoChange, await s.Purchases.ApplySubscriptionUpdateAsync(row, renewed, "test"));

            // Ran out and will not renew → Expired.
            var ended = new ReceiptVerification { Outcome = VerifyOutcome.Valid, IsSubscription = true, SubscriptionExpiresUtc = DateTime.UtcNow.AddMinutes(-5), AutoRenew = false };
            row.SubscriptionExpiresAt = DateTime.UtcNow.AddMinutes(-5);
            Assert.Equal(SubscriptionUpdate.Expired, await s.Purchases.ApplySubscriptionUpdateAsync(row, ended, "test"));
            Assert.Equal(StorePurchaseStatus.Expired, row.Status);

            // Revoked by the store → Refunded (the row was Expired, not Granted → NoChange, the refund policy only reverses granted rows).
            var revoked = new ReceiptVerification { Outcome = VerifyOutcome.Invalid, Revoked = true, Reason = "revoked" };
            Assert.Equal(SubscriptionUpdate.NoChange, await s.Purchases.ApplySubscriptionUpdateAsync(row, revoked, "test"));
            row.Status = StorePurchaseStatus.Granted; await s.Db.SaveChangesAsync();
            Assert.Equal(SubscriptionUpdate.Revoked, await s.Purchases.ApplySubscriptionUpdateAsync(row, revoked, "test"));
            Assert.Equal(StorePurchaseStatus.Refunded, (await s.Db.StorePurchases.AsNoTracking().SingleAsync(p => p.Id == row.Id)).Status);
        }
    }
}
