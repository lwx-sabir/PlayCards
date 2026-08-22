using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Khela.Game.Services.Store.Verification
{
    /// <summary>A one-time product purchase as Google reports it (purchases.products.get).</summary>
    public sealed class GoogleProductPurchaseInfo
    {
        /// <summary>0 = Purchased, 1 = Cancelled, 2 = Pending.</summary>
        public int PurchaseState { get; set; }
        /// <summary>0 = yet to be consumed, 1 = consumed.</summary>
        public int? ConsumptionState { get; set; }
        /// <summary>0 = yet to be acknowledged, 1 = acknowledged.</summary>
        public int? AcknowledgementState { get; set; }
        public string OrderId { get; set; }
        /// <summary>0 = Test (licence tester), 1 = Promo, 2 = Rewarded. Null = a normal paid purchase.</summary>
        public int? PurchaseType { get; set; }
        public DateTime? PurchaseTimeUtc { get; set; }
        public string RegionCode { get; set; }
        public string ObfuscatedExternalAccountId { get; set; }
        public int Quantity { get; set; } = 1;
        public string ProductId { get; set; }
        public string PurchaseToken { get; set; }
        public string RawJson { get; set; }
    }

    /// <summary>A subscription purchase as Google reports it (purchases.subscriptionsv2.get).</summary>
    public sealed class GoogleSubscriptionInfo
    {
        /// <summary>SUBSCRIPTION_STATE_ACTIVE / _CANCELED / _IN_GRACE_PERIOD / _ON_HOLD / _PAUSED / _EXPIRED / _PENDING / _PENDING_PURCHASE_CANCELED …</summary>
        public string SubscriptionState { get; set; }
        /// <summary>ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED / _PENDING.</summary>
        public string AcknowledgementState { get; set; }
        public string LinkedPurchaseToken { get; set; }
        public string LatestOrderId { get; set; }
        public bool IsTest { get; set; }
        public string RegionCode { get; set; }
        public string ObfuscatedExternalAccountId { get; set; }
        public DateTime? StartTimeUtc { get; set; }
        public DateTime? ExpiryTimeUtc { get; set; }
        public string ProductId { get; set; }
        public bool AutoRenewing { get; set; }
        public string RawJson { get; set; }
    }

    public sealed class GoogleVoidedPurchaseInfo
    {
        public string PurchaseToken { get; set; }
        public string OrderId { get; set; }
        public DateTime? PurchaseTimeUtc { get; set; }
        public DateTime? VoidedTimeUtc { get; set; }
        /// <summary>0 = User, 1 = Developer, 2 = Google.</summary>
        public int? VoidedSource { get; set; }
        /// <summary>0 = Other, 1 = Remorse, 2 = Not received, 3 = Defective, 4 = Accidental purchase, 5 = Fraud, 6 = Friendly fraud, 7 = Chargeback.</summary>
        public int? VoidedReason { get; set; }
    }

    /// <summary>Thrown by the gateway when Google answered with a definitive "no such purchase" / bad request.</summary>
    public sealed class GooglePlayNotFoundException : Exception
    {
        public GooglePlayNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// The Google Play Developer API surface the store needs, as an interface so the verifier and the reconciler are
    /// testable against a fake. Everything that talks to Google lives in <see cref="GooglePlayGateway"/> alone.
    /// Transient failures (network, 5xx, quota, auth) surface as exceptions other than <see cref="GooglePlayNotFoundException"/>.
    /// </summary>
    public interface IGooglePlayGateway
    {
        bool IsConfigured { get; }
        string PackageName { get; }
        Task<GoogleProductPurchaseInfo> GetProductPurchaseAsync(string productId, string purchaseToken, CancellationToken ct);
        Task<GoogleSubscriptionInfo> GetSubscriptionAsync(string purchaseToken, CancellationToken ct);
        Task AcknowledgeProductAsync(string productId, string purchaseToken, CancellationToken ct);
        Task ConsumeProductAsync(string productId, string purchaseToken, CancellationToken ct);
        Task AcknowledgeSubscriptionAsync(string subscriptionId, string purchaseToken, CancellationToken ct);
        Task<List<GoogleVoidedPurchaseInfo>> ListVoidedPurchasesAsync(DateTime sinceUtc, CancellationToken ct);
    }

    /// <summary>
    /// Android Publisher v3 over a Play-only service account (<c>Store:GooglePlay:ServiceAccountJsonPath</c>; scope
    /// <c>androidpublisher</c>). The service account must be invited in Play Console ▸ Users &amp; permissions with
    /// "View financial data" + "Manage orders and subscriptions" — and Google can answer 401/"insufficient permissions"
    /// for up to ~24 h after granting (editing + saving any product once usually unsticks it).
    /// </summary>
    public sealed class GooglePlayGateway : IGooglePlayGateway, IDisposable
    {
        private readonly IOptionsMonitor<StoreOptions> _options;
        private readonly ILogger<GooglePlayGateway> _logger;
        private readonly object _gate = new object();
        private AndroidPublisherService _service;

        public GooglePlayGateway(IOptionsMonitor<StoreOptions> options, ILogger<GooglePlayGateway> logger)
        {
            _options = options; _logger = logger;
        }

        public string PackageName => _options.CurrentValue.GooglePlay.PackageName;

        public bool IsConfigured
        {
            get
            {
                var o = _options.CurrentValue.GooglePlay;
                return !string.IsNullOrWhiteSpace(o.PackageName) && !string.IsNullOrWhiteSpace(o.ServiceAccountJsonPath) && File.Exists(Resolve(o.ServiceAccountJsonPath));
            }
        }

        private static string Resolve(string path)
            => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

        private AndroidPublisherService Service()
        {
            lock (_gate)
            {
                if (_service != null) return _service;
                var o = _options.CurrentValue.GooglePlay;
                var credential = GoogleCredential.FromFile(Resolve(o.ServiceAccountJsonPath)).CreateScoped(AndroidPublisherService.Scope.Androidpublisher);
                _service = new AndroidPublisherService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "Khela.Game",
                });
                return _service;
            }
        }

        public async Task<GoogleProductPurchaseInfo> GetProductPurchaseAsync(string productId, string purchaseToken, CancellationToken ct)
        {
            try
            {
                var p = await Service().Purchases.Products.Get(PackageName, productId, purchaseToken).ExecuteAsync(ct);
                return new GoogleProductPurchaseInfo
                {
                    PurchaseState = p.PurchaseState ?? 0,
                    ConsumptionState = p.ConsumptionState,
                    AcknowledgementState = p.AcknowledgementState,
                    OrderId = p.OrderId,
                    PurchaseType = p.PurchaseType,
                    PurchaseTimeUtc = StoreMath.FromUnixMs(p.PurchaseTimeMillis),
                    RegionCode = p.RegionCode,
                    ObfuscatedExternalAccountId = p.ObfuscatedExternalAccountId,
                    Quantity = p.Quantity ?? 1,
                    ProductId = p.ProductId ?? productId,
                    PurchaseToken = p.PurchaseToken ?? purchaseToken,
                    RawJson = JsonSerializer.Serialize(p),
                };
            }
            catch (GoogleApiException ex) when (IsNotFound(ex)) { throw new GooglePlayNotFoundException(ex.Message); }
        }

        public async Task<GoogleSubscriptionInfo> GetSubscriptionAsync(string purchaseToken, CancellationToken ct)
        {
            try
            {
                var s = await Service().Purchases.Subscriptionsv2.Get(PackageName, purchaseToken).ExecuteAsync(ct);
                SubscriptionPurchaseLineItem line = null;
                if (s.LineItems != null)
                    foreach (var li in s.LineItems) { if (line == null || (li.ExpiryTimeDateTimeOffset ?? DateTimeOffset.MinValue) > (line.ExpiryTimeDateTimeOffset ?? DateTimeOffset.MinValue)) line = li; }
                return new GoogleSubscriptionInfo
                {
                    SubscriptionState = s.SubscriptionState,
                    AcknowledgementState = s.AcknowledgementState,
                    LinkedPurchaseToken = s.LinkedPurchaseToken,
                    LatestOrderId = line?.LatestSuccessfulOrderId,
                    IsTest = s.TestPurchase != null,
                    RegionCode = s.RegionCode,
                    ObfuscatedExternalAccountId = s.ExternalAccountIdentifiers?.ObfuscatedExternalAccountId,
                    StartTimeUtc = s.StartTimeDateTimeOffset?.UtcDateTime,
                    ExpiryTimeUtc = line?.ExpiryTimeDateTimeOffset?.UtcDateTime,
                    ProductId = line?.ProductId,
                    AutoRenewing = line?.AutoRenewingPlan?.AutoRenewEnabled ?? false,
                    RawJson = JsonSerializer.Serialize(s),
                };
            }
            catch (GoogleApiException ex) when (IsNotFound(ex)) { throw new GooglePlayNotFoundException(ex.Message); }
        }

        public async Task AcknowledgeProductAsync(string productId, string purchaseToken, CancellationToken ct)
            => await Service().Purchases.Products.Acknowledge(new ProductPurchasesAcknowledgeRequest(), PackageName, productId, purchaseToken).ExecuteAsync(ct);

        public async Task ConsumeProductAsync(string productId, string purchaseToken, CancellationToken ct)
            => await Service().Purchases.Products.Consume(PackageName, productId, purchaseToken).ExecuteAsync(ct);

        public async Task AcknowledgeSubscriptionAsync(string subscriptionId, string purchaseToken, CancellationToken ct)
            => await Service().Purchases.Subscriptions.Acknowledge(new SubscriptionPurchasesAcknowledgeRequest(), PackageName, subscriptionId, purchaseToken).ExecuteAsync(ct);

        public async Task<List<GoogleVoidedPurchaseInfo>> ListVoidedPurchasesAsync(DateTime sinceUtc, CancellationToken ct)
        {
            var result = new List<GoogleVoidedPurchaseInfo>();
            string pageToken = null;
            do
            {
                var req = Service().Purchases.Voidedpurchases.List(PackageName);
                req.StartTime = new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                req.Type = 1;   // 0 = in-app products only, 1 = products AND subscriptions
                if (pageToken != null) req.Token = pageToken;
                var page = await req.ExecuteAsync(ct);
                if (page.VoidedPurchases != null)
                    foreach (var v in page.VoidedPurchases)
                        result.Add(new GoogleVoidedPurchaseInfo
                        {
                            PurchaseToken = v.PurchaseToken,
                            OrderId = v.OrderId,
                            PurchaseTimeUtc = StoreMath.FromUnixMs(v.PurchaseTimeMillis),
                            VoidedTimeUtc = StoreMath.FromUnixMs(v.VoidedTimeMillis),
                            VoidedSource = v.VoidedSource,
                            VoidedReason = v.VoidedReason,
                        });
                pageToken = page.TokenPagination?.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken) && result.Count < 5000);
            return result;
        }

        private static bool IsNotFound(GoogleApiException ex)
            => ex.HttpStatusCode == HttpStatusCode.NotFound || ex.HttpStatusCode == HttpStatusCode.Gone || ex.HttpStatusCode == HttpStatusCode.BadRequest;

        public void Dispose() { lock (_gate) { _service?.Dispose(); _service = null; } }
    }
}
