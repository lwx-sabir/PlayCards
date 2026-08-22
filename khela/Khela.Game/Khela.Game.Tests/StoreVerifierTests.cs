using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Database.Models;
using Khela.Game.Services.Store;
using Khela.Game.Services.Store.Verification;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// The Google verifier's state mapping — the thing that decides whether a subscription is a live entitlement, a
    /// payment still in flight, or a REVOCATION. Getting "revoked" wrong is not a cosmetic error: the subscription seam
    /// hands a revocation straight to the refund policy, which revokes the golden window (expiring uncollected paid
    /// rewards) and reverses any currency lines. A subscription that merely ENDED must never look like that.
    /// </summary>
    public class StoreVerifierTests
    {
        private sealed class FakeGoogleGateway : IGooglePlayGateway
        {
            public string SubscriptionState = "SUBSCRIPTION_STATE_ACTIVE";
            public bool IsConfigured => true;
            public string PackageName => "com.casuallabinteractive.khela";

            public Task<GoogleProductPurchaseInfo> GetProductPurchaseAsync(string productId, string purchaseToken, CancellationToken ct)
                => Task.FromResult(new GoogleProductPurchaseInfo { PurchaseState = 0, ProductId = productId, PurchaseToken = purchaseToken, RawJson = "{}" });

            public Task<GoogleSubscriptionInfo> GetSubscriptionAsync(string purchaseToken, CancellationToken ct)
                => Task.FromResult(new GoogleSubscriptionInfo
                {
                    SubscriptionState = SubscriptionState,
                    AcknowledgementState = "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED",
                    ProductId = "golden_pass",
                    StartTimeUtc = DateTime.UtcNow.AddDays(-30),
                    ExpiryTimeUtc = DateTime.UtcNow.AddMinutes(-1),
                    AutoRenewing = false,
                    RawJson = "{}",
                });

            public Task AcknowledgeProductAsync(string productId, string purchaseToken, CancellationToken ct) => Task.CompletedTask;
            public Task ConsumeProductAsync(string productId, string purchaseToken, CancellationToken ct) => Task.CompletedTask;
            public Task AcknowledgeSubscriptionAsync(string subscriptionId, string purchaseToken, CancellationToken ct) => Task.CompletedTask;
            public Task<List<GoogleVoidedPurchaseInfo>> ListVoidedPurchasesAsync(DateTime sinceUtc, CancellationToken ct)
                => Task.FromResult(new List<GoogleVoidedPurchaseInfo>());
        }

        /// <summary>A real Unity unified receipt for Google Play: JSON, inside a JSON string, inside a JSON string.
        /// Built with the serializer rather than by hand — the triple nesting is exactly where a hand-written fixture
        /// goes quietly wrong and every assertion then measures the parser instead of the mapping.</summary>
        private static RedeemPurchaseRequest GoogleRequest(string productId = "golden_pass")
        {
            const string orderId = "GPA.1111-2222-3333-44444";
            var inAppPurchaseData = System.Text.Json.JsonSerializer.Serialize(new
            {
                orderId,
                packageName = "com.casuallabinteractive.khela",
                productId,
                purchaseTime = 1756000000000L,
                purchaseState = 0,
                purchaseToken = "tok-abc",
                acknowledged = true,
            });
            var payload = System.Text.Json.JsonSerializer.Serialize(new { json = inAppPurchaseData, signature = "SIG==" });
            var receipt = System.Text.Json.JsonSerializer.Serialize(new { Store = "GooglePlay", TransactionID = orderId, Payload = payload });
            return new RedeemPurchaseRequest
            {
                Platform = StorePlatform.GooglePlay,
                ProductId = productId,
                StoreProductId = productId,
                TransactionId = orderId,
                Receipt = receipt,
            };
        }

        private static (GooglePlayReceiptVerifier Verifier, FakeGoogleGateway Gateway) NewVerifier()
        {
            var gateway = new FakeGoogleGateway();
            var options = new StaticOptionsMonitor<StoreOptions>(new StoreOptions());
            return (new GooglePlayReceiptVerifier(gateway, options, new NoRedis(), null, NullLogger<GooglePlayReceiptVerifier>.Instance), gateway);
        }

        [Theory]
        [InlineData("SUBSCRIPTION_STATE_ACTIVE")]
        [InlineData("SUBSCRIPTION_STATE_CANCELED")]          // no renewal, still paid until expiry
        [InlineData("SUBSCRIPTION_STATE_IN_GRACE_PERIOD")]
        [InlineData("SUBSCRIPTION_STATE_EXPIRED")]           // simply ended — NOT a revocation
        [InlineData("SUBSCRIPTION_STATE_ON_HOLD")]           // recoverable
        [InlineData("SUBSCRIPTION_STATE_PAUSED")]            // recoverable
        public async Task SubscriptionLifecycleStates_AreValid_AndNeverRevoked(string state)
        {
            var (verifier, gateway) = NewVerifier();
            gateway.SubscriptionState = state;
            var v = await verifier.VerifyAsync(GoogleRequest(), StoreProductType.Subscription, default);

            Assert.Equal(VerifyOutcome.Valid, v.Outcome);
            Assert.False(v.Revoked, $"{state} must never be reported as revoked — that routes it to the refund policy.");
        }

        [Fact]
        public async Task PendingSubscription_IsPendingPayment_AndAbandonedPurchaseIsInvalid()
        {
            var (verifier, gateway) = NewVerifier();

            gateway.SubscriptionState = "SUBSCRIPTION_STATE_PENDING";
            var pending = await verifier.VerifyAsync(GoogleRequest(), StoreProductType.Subscription, default);
            Assert.Equal(VerifyOutcome.PendingPayment, pending.Outcome);
            Assert.False(pending.Revoked);

            // Ends with "CANCELED", so it must be tested before the ordinary states or an abandoned purchase reads as live.
            gateway.SubscriptionState = "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED";
            var abandoned = await verifier.VerifyAsync(GoogleRequest(), StoreProductType.Subscription, default);
            Assert.Equal(VerifyOutcome.Invalid, abandoned.Outcome);
        }

        [Fact]
        public async Task OneTimeProductStates_MapCorrectly()
        {
            var (verifier, _) = NewVerifier();
            var v = await verifier.VerifyAsync(GoogleRequest("chips_01"), StoreProductType.Consumable, default);
            Assert.Equal(VerifyOutcome.Valid, v.Outcome);
            Assert.Equal("chips_01", v.StoreProductId);
            Assert.Equal("tok-abc", v.StoreTransactionId);     // the purchase TOKEN is the key, never the order id
            Assert.Equal("GPA.1111-2222-3333-44444", v.StoreOrderId);
            Assert.False(v.Revoked);
        }

        [Fact]
        public void ExtractedKeyIsThePurchaseToken_NotTheOrderId()
        {
            var (verifier, _) = NewVerifier();
            Assert.True(verifier.TryExtractTransactionKey(GoogleRequest(), out var key, out var err), err);
            Assert.Equal("tok-abc", key);
        }
    }
}
