using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Database.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mimo.AppStoreServerLibrary;
using Mimo.AppStoreServerLibrary.Exceptions;
using Mimo.AppStoreServerLibrary.Models;

namespace Khela.Game.Services.Store.Verification
{
    /// <summary>
    /// Apple App Store, StoreKit 2. Input = <c>order.Info.Apple.jwsRepresentation</c> (the signed transaction); verified
    /// LOCALLY — ES256 signature + x5c chain to Apple Root CA G3 (<c>Store:AppStore:RootCertPath</c>) — by the .NET port of
    /// Apple's App Store Server Library, then the payload is checked against our bundle id / product. Sandbox transactions
    /// are accepted (flagged IsTest) when <c>Store:AppStore:AcceptSandbox</c>: App Review buys with sandbox accounts on the
    /// production build, and sandbox accounts are developer-created. <see cref="RefreshAsync"/> uses the App Store Server
    /// API (Issuer/Key/.p8) when configured. Ships built but <c>Store:AppStore:Enabled=false</c> until an iOS build exists.
    /// </summary>
    public sealed class AppStoreReceiptVerifier : IStoreReceiptVerifier
    {
        private readonly IOptionsMonitor<StoreOptions> _options;
        private readonly IHttpClientFactory _http;
        private readonly ILogger<AppStoreReceiptVerifier> _logger;
        private readonly object _gate = new object();
        private SignedDataVerifier _production;
        private SignedDataVerifier _sandbox;
        private byte[] _rootCert;

        public AppStoreReceiptVerifier(IOptionsMonitor<StoreOptions> options, IHttpClientFactory http, ILogger<AppStoreReceiptVerifier> logger)
        {
            _options = options; _http = http; _logger = logger;
        }

        public StorePlatform Platform => StorePlatform.AppStore;

        public bool IsConfigured
        {
            get
            {
                var o = _options.CurrentValue.AppStore;
                return !string.IsNullOrWhiteSpace(o.BundleId) && !string.IsNullOrWhiteSpace(o.RootCertPath) && File.Exists(Resolve(o.RootCertPath));
            }
        }

        private static string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

        public bool TryExtractTransactionKey(RedeemPurchaseRequest req, out string storeTransactionId, out string error)
        {
            storeTransactionId = null; error = null;
            var jws = req?.Jws;
            if (string.IsNullOrWhiteSpace(jws)) { error = "Apple purchase has no jwsRepresentation."; return false; }
            using var doc = StoreMath.DecodeJwsPayloadUnverified(jws);
            if (doc == null) { error = "jwsRepresentation is not a JWS."; return false; }
            if (doc.RootElement.TryGetProperty("transactionId", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
            {
                storeTransactionId = t.GetString().Trim();
                return true;
            }
            error = "JWS payload has no transactionId.";
            return false;
        }

        public async Task<ReceiptVerification> VerifyAsync(RedeemPurchaseRequest req, StoreProductType productType, CancellationToken ct)
        {
            if (!IsConfigured) return ReceiptVerification.Transient("App Store verifier is not configured (bundle id / root certificate).");
            if (!TryExtractTransactionKey(req, out _, out var err)) return ReceiptVerification.Invalid(err);
            var o = _options.CurrentValue.AppStore;

            JwsTransactionDecodedPayload payload = null;
            string environment = null;
            try
            {
                try
                {
                    payload = await Verifier(false).VerifyAndDecodeTransaction(req.Jws);
                    environment = "Production";
                }
                catch (VerificationException) when (o.AcceptSandbox)
                {
                    payload = await Verifier(true).VerifyAndDecodeTransaction(req.Jws);
                    environment = "Sandbox";
                }
            }
            catch (VerificationException ex)
            {
                return ReceiptVerification.Invalid("Apple signature/claims rejected: " + ex.Message);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "App Store verification transient failure.");
                return ReceiptVerification.Transient("App Store verification could not run: " + ex.Message);
            }

            return Map(payload, environment, productType);
        }

        private ReceiptVerification Map(JwsTransactionDecodedPayload p, string environment, StoreProductType productType)
        {
            var o = _options.CurrentValue.AppStore;
            if (!string.IsNullOrWhiteSpace(p.BundleId) && !string.Equals(p.BundleId, o.BundleId, StringComparison.Ordinal))
                return ReceiptVerification.Invalid($"Bundle '{p.BundleId}' is not ours.");
            if (string.IsNullOrWhiteSpace(p.TransactionId)) return ReceiptVerification.Invalid("Transaction has no id.");

            bool sandbox = string.Equals(environment, "Sandbox", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase);
            if (sandbox && !o.AcceptSandbox) return ReceiptVerification.Invalid("Sandbox transactions are not accepted on this server.");

            bool isSubscription = productType == StoreProductType.Subscription
                               || (p.Type ?? "").IndexOf("Subscription", StringComparison.OrdinalIgnoreCase) >= 0;
            var expires = StoreMath.FromUnixMs(p.ExpiresDate);
            var revoked = p.RevocationDate > 0;

            var v = new ReceiptVerification
            {
                Outcome = revoked ? VerifyOutcome.Invalid : VerifyOutcome.Valid,
                Reason = revoked ? "Transaction was revoked/refunded by Apple." : null,
                StoreProductId = p.ProductId,
                StoreTransactionId = p.TransactionId,
                StoreOrderId = p.TransactionId,
                OriginalTransactionId = p.OriginalTransactionId,
                Quantity = Math.Max(1, p.Quantity),
                IsTest = sandbox,
                Environment = sandbox ? "Sandbox" : "Production",
                RegionCode = p.Storefront,
                ObfuscatedAccountId = p.AppAccountToken,
                PurchaseTimeUtc = StoreMath.FromUnixMs(p.PurchaseDate),
                PriceMicros = p.Price > 0 ? p.Price * 1000 : (long?)null,   // Apple price is in milliunits
                PriceCurrency = p.Currency,
                IsSubscription = isSubscription,
                SubscriptionStartUtc = isSubscription ? StoreMath.FromUnixMs(p.PurchaseDate) : null,
                SubscriptionExpiresUtc = isSubscription ? expires : null,
                AutoRenew = isSubscription && !revoked,   // renewal status lives in the renewal-info JWS; the reconciler refreshes it
                Revoked = revoked,
                RawJson = JsonSerializer.Serialize(p),
            };
            if (isSubscription && v.Outcome == VerifyOutcome.Valid && expires.HasValue && expires.Value <= DateTime.UtcNow)
            {
                // An already-expired window is not a deliverable purchase (a restore of an old month, say): honour the window
                // as-is — GrantGoldenAsync records it and the pass stays non-golden. Still Valid so idempotent restores don't loop.
            }
            return v;
        }

        public Task<bool> AcknowledgeAsync(StorePurchase purchase, CancellationToken ct) => Task.FromResult(true);   // StoreKit 2: the client finishes; nothing server-side

        /// <summary>
        /// Verify an App Store Server Notification v2 (<c>signedPayload</c>) and flatten it: the notification type/subtype/uuid
        /// plus the signed transaction inside it (verified too). Production first, then Sandbox when accepted. Null when the
        /// signature or claims are rejected — the caller must then ignore the body entirely.
        /// </summary>
        public async Task<AppleNotification> DecodeNotificationAsync(string signedPayload, CancellationToken ct)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(signedPayload)) return null;
            var o = _options.CurrentValue.AppStore;
            ResponseBodyV2DecodedPayload body = null;
            bool sandbox = false;
            try
            {
                try { body = await Verifier(false).VerifyAndDecodeNotification(signedPayload); }
                catch (VerificationException) when (o.AcceptSandbox) { body = await Verifier(true).VerifyAndDecodeNotification(signedPayload); sandbox = true; }
            }
            catch (VerificationException ex) { _logger.LogWarning("Apple notification rejected: {Message}", ex.Message); return null; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "Apple notification could not be verified."); return null; }
            if (body == null) return null;

            var n = new AppleNotification
            {
                NotificationType = body.NotificationType,
                Subtype = body.Subtype,
                NotificationUuid = body.NotificationUuid == Guid.Empty ? null : body.NotificationUuid.ToString("D"),
                Environment = body.Data?.Environment ?? (sandbox ? "Sandbox" : "Production"),
                BundleId = body.Data?.BundleId,
                RawJson = JsonSerializer.Serialize(new { body.NotificationType, body.Subtype, body.NotificationUuid, body.Version, body.SignedDate, data = new { body.Data?.BundleId, body.Data?.Environment, body.Data?.AppAppleId } }),
            };
            if (!string.IsNullOrWhiteSpace(n.BundleId) && !string.Equals(n.BundleId, o.BundleId, StringComparison.Ordinal))
            {
                _logger.LogWarning("Apple notification for another bundle '{Bundle}' ignored.", n.BundleId);
                return null;
            }
            if (!string.IsNullOrWhiteSpace(body.Data?.SignedTransactionInfo))
            {
                try
                {
                    var tx = await Verifier(sandbox).VerifyAndDecodeTransaction(body.Data.SignedTransactionInfo);
                    n.TransactionId = tx.TransactionId;
                    n.OriginalTransactionId = tx.OriginalTransactionId;
                    n.ProductId = tx.ProductId;
                    n.Type = tx.Type;
                    n.ExpiresUtc = StoreMath.FromUnixMs(tx.ExpiresDate);
                    n.RevocationUtc = StoreMath.FromUnixMs(tx.RevocationDate);
                }
                catch (VerificationException ex) { _logger.LogWarning("Apple notification transaction rejected: {Message}", ex.Message); return null; }
            }
            if (!string.IsNullOrWhiteSpace(body.Data?.SignedRenewalInfo))
            {
                try
                {
                    var renewal = await Verifier(sandbox).VerifyAndDecodeRenewalInfo(body.Data.SignedRenewalInfo);
                    n.AutoRenew = renewal.AutoRenewStatus == 1;
                    n.OriginalTransactionId ??= renewal.OriginalTransactionId;
                    n.ProductId ??= renewal.ProductId;
                }
                catch (VerificationException) { /* renewal info is advisory; the transaction decides */ }
            }
            return n;
        }

        public async Task<ReceiptVerification> RefreshAsync(StorePurchase purchase, CancellationToken ct)
        {
            var o = _options.CurrentValue.AppStore;
            if (!IsConfigured || purchase == null) return null;
            if (string.IsNullOrWhiteSpace(o.IssuerId) || string.IsNullOrWhiteSpace(o.KeyId) || string.IsNullOrWhiteSpace(o.PrivateKeyPath) || !File.Exists(Resolve(o.PrivateKeyPath)))
                return null;   // no App Store Server API credentials → can't refresh; the webhook is the other channel
            try
            {
                var key = await File.ReadAllTextAsync(Resolve(o.PrivateKeyPath), ct);
                bool sandbox = string.Equals(purchase.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase);
                var client = new AppStoreServerApiClient(key, o.KeyId, o.IssuerId, o.BundleId, Env(sandbox), _http.CreateClient("appstore"));
                var info = await client.GetTransactionInfo(purchase.StoreTransactionId);
                if (info?.SignedTransactionInfo == null) return ReceiptVerification.Transient("Empty transaction info from Apple.");
                var payload = await Verifier(sandbox).VerifyAndDecodeTransaction(info.SignedTransactionInfo);
                return Map(payload, sandbox ? "Sandbox" : "Production", purchase.ProductType);
            }
            catch (VerificationException ex) { return ReceiptVerification.Invalid("Apple refresh rejected: " + ex.Message); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "App Store refresh failed for purchase {Id}.", purchase.Id);
                return ReceiptVerification.Transient(ex.Message);
            }
        }

        private SignedDataVerifier Verifier(bool sandbox)
        {
            lock (_gate)
            {
                var o = _options.CurrentValue.AppStore;
                _rootCert ??= File.ReadAllBytes(Resolve(o.RootCertPath));
                if (sandbox) return _sandbox ??= new SignedDataVerifier(new[] { _rootCert }, o.EnableOnlineChecks, Env(true), o.BundleId);
                return _production ??= new SignedDataVerifier(new[] { _rootCert }, o.EnableOnlineChecks, Env(false), o.BundleId);
            }
        }

        /// <summary>The library models environments as a record with static well-known instances; resolve by name so a rename in a
        /// future package version fails loudly here rather than silently verifying against the wrong host.</summary>
        private static AppStoreEnvironment Env(bool sandbox)
        {
            var name = sandbox ? "Sandbox" : "Production";
            var t = typeof(AppStoreEnvironment);
            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (prop != null) return (AppStoreEnvironment)prop.GetValue(null);
            var field = t.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (field != null) return (AppStoreEnvironment)field.GetValue(null);
            throw new InvalidOperationException("Mimo.AppStoreServerLibrary has no AppStoreEnvironment." + name);
        }
    }
}
