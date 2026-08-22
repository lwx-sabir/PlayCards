using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Khela.Common.Store;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Loyalty;
using Khela.Game.Services.Pass;
using Khela.Game.Services.Progression;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Rewards;
using Khela.Game.Services.Store.Grants;
using Khela.Game.Services.Store.Verification;
using Khela.Game.Services.Vip;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Khela.Game.Services.Store
{
    /// <summary>
    /// The purchase spine (docs/IAP_SPEC.md §5): intent → redeem (reserve → verify → grant → complete) → restore → history,
    /// plus the re-drive and refund entry points the reconciler and the admin use.
    /// </summary>
    public interface IStorePurchaseService
    {
        /// <summary>"May I buy this now?" — before the store sheet opens. Nothing durable is written.</summary>
        Task<StoreIntentResultDto> IntentAsync(Guid userId, StoreIntentRequest req, CancellationToken ct = default);

        /// <summary>The money path for a CLIENT-delivered store receipt. Idempotent on the store transaction; safe to call again with the same receipt at any time.</summary>
        Task<RedeemPurchaseResultDto> RedeemAsync(Guid userId, RedeemPurchaseRequest req, CancellationToken ct = default);

        /// <summary>
        /// The RAIL-AGNOSTIC money path: fulfil a purchase that has ALREADY been verified by some adapter — a store verifier,
        /// or a web-checkout / bKash / Stripe webhook handler that turned the provider's confirmation into a
        /// <see cref="ReceiptVerification"/>. Nothing downstream knows or cares where the money came from: the same
        /// reserve → grant → complete, the same idempotency on (platform, transaction id), the same catalog snapshot,
        /// effects, refund policy and spend hooks. <see cref="RedeemAsync"/> is this method with a receipt verifier in front.
        /// </summary>
        Task<RedeemPurchaseResultDto> RedeemVerifiedAsync(Guid userId, StorePlatform platform, string productId, ReceiptVerification verified,
            string rawEvidence = null, string clientPurchaseId = null, string clientVersion = null, CancellationToken ct = default);

        /// <summary>Re-run a batch of receipts (subscriptions / non-consumables) through the same idempotent path.</summary>
        Task<StoreRestoreResultDto> RestoreAsync(Guid userId, StoreRestoreRequest req, CancellationToken ct = default);

        Task<List<StorePurchaseDto>> GetHistoryAsync(Guid userId, int take = 50, CancellationToken ct = default);

        /// <summary>Re-drive a stuck row (Pending/Verified/Granted-but-incomplete) from its stored receipt. Used by the reconciler and the admin.</summary>
        Task<RedeemPurchaseResultDto> RedriveAsync(Guid purchaseId, CancellationToken ct = default);

        /// <summary>
        /// The store told us a purchase was refunded/voided: reverse what the refund policy allows, then mark the row. Idempotent.
        /// <paramref name="partial"/> = a QUANTITY-BASED partial refund (Google refundType 2): the store took back some units, not
        /// the purchase. Balances are never touched in that case — a fraction of a spent correlation id cannot be reversed — so it
        /// is recorded and flagged for an admin to comp instead.
        /// </summary>
        Task<bool> MarkRefundedAsync(Guid purchaseId, string source, string reason, bool partial = false, CancellationToken ct = default);

        /// <summary>The purchase row for a store transaction key (Google purchaseToken / Apple transactionId), or null.</summary>
        Task<StorePurchase> FindAsync(StorePlatform platform, string storeTransactionId, CancellationToken ct = default);

        /// <summary>The latest subscription purchase row sharing an original transaction id (Apple originalTransactionId / Google token hash), or null.</summary>
        Task<StorePurchase> FindByOriginalTransactionAsync(StorePlatform platform, string originalTransactionId, CancellationToken ct = default);

        /// <summary>
        /// Apply a FRESH verification of a subscription purchase (from a webhook or the reconciler's refresh): a longer
        /// window appends a new golden entitlement window under the same original transaction id; a revocation/refund
        /// marks the row refunded; an ended, non-renewing window marks it expired. Idempotent on the window end date.
        /// </summary>
        Task<SubscriptionUpdate> ApplySubscriptionUpdateAsync(StorePurchase row, ReceiptVerification fresh, string source, CancellationToken ct = default);
    }

    /// <summary>What <see cref="IStorePurchaseService.ApplySubscriptionUpdateAsync"/> did.</summary>
    public enum SubscriptionUpdate { NoChange = 0, Extended = 1, Revoked = 2, Expired = 3 }

    public sealed class StorePurchaseService : IStorePurchaseService
    {
        private readonly AppDbContext _db;
        private readonly IStoreCatalogService _catalog;
        private readonly StoreVerifierRegistry _verifiers;
        private readonly Dictionary<string, IStoreGrantHandler> _handlers;
        private readonly IWalletService _wallet;
        private readonly IRewardGrantService _rewards;
        private readonly IVipService _vip;
        private readonly ILoyaltyService _loyalty;
        private readonly IProgressionService _progression;
        private readonly IPassService _pass;
        private readonly IRedisService _redis;
        private readonly IConfiguration _config;
        private readonly IOptionsMonitor<StoreOptions> _options;
        private readonly ILogger<StorePurchaseService> _logger;

        public StorePurchaseService(AppDbContext db, IStoreCatalogService catalog, StoreVerifierRegistry verifiers,
            IEnumerable<IStoreGrantHandler> handlers, IWalletService wallet, IRewardGrantService rewards, IVipService vip,
            ILoyaltyService loyalty, IProgressionService progression, IPassService pass, IRedisService redis,
            IConfiguration config, IOptionsMonitor<StoreOptions> options, ILogger<StorePurchaseService> logger)
        {
            _db = db; _catalog = catalog; _verifiers = verifiers; _wallet = wallet; _rewards = rewards; _vip = vip;
            _loyalty = loyalty; _progression = progression; _pass = pass; _redis = redis; _config = config; _options = options; _logger = logger;
            _handlers = new Dictionary<string, IStoreGrantHandler>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in handlers ?? Array.Empty<IStoreGrantHandler>()) _handlers[h.Effect] = h;
        }

        /// <summary>Effect types a grant handler exists for — the catalog validator checks against this set.</summary>
        public ISet<string> HandlerEffects => new HashSet<string>(_handlers.Keys, StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ intent

        public async Task<StoreIntentResultDto> IntentAsync(Guid userId, StoreIntentRequest req, CancellationToken ct = default)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ProductId)) return new StoreIntentResultDto { Ok = false, Error = "Missing product." };
            var platform = req.Platform == StorePlatform.Unknown ? StorePlatform.Fake : req.Platform;
            var check = await _catalog.CheckAsync(platform, userId, req.ProductId);
            if (!check.Ok) return new StoreIntentResultDto { Ok = false, Error = check.Reason, StoreProductId = check.StoreProductId };

            // Effect-specific eligibility: the piggy must be buyable in that option; golden must not already be golden.
            var p = check.Product;
            if (p.Effect != null && string.Equals(p.Effect.Type, StoreCatalog.EffectGoldenPass, StringComparison.OrdinalIgnoreCase))
            {
                var passKey = string.IsNullOrWhiteSpace(p.Effect.Arg) ? PassCatalog.MonthlyKey : p.Effect.Arg;
                if (await _pass.IsGoldenAsync(userId, passKey, DateTime.UtcNow))
                    return new StoreIntentResultDto { Ok = false, Error = "You already have the Golden Pass.", StoreProductId = check.StoreProductId };
            }
            if (p.Effect != null && string.Equals(p.Effect.Type, StoreCatalog.EffectPiggyBreak, StringComparison.OrdinalIgnoreCase))
            {
                // Full / FullDouble need a full bank; Early needs a non-empty, not-yet-full one. The piggy state is the truth.
                var state = await _db.PlayerPiggyBanks.AsNoTracking().Where(b => b.UserId == userId).Select(b => new { b.Amount, b.MaxAmount }).FirstOrDefaultAsync(ct);
                StoreCatalog.TryParsePiggyOption(p.Effect.Arg, out var option);
                bool full = state != null && state.MaxAmount > 0m && state.Amount >= state.MaxAmount;
                if (option != Khela.Common.Piggy.PiggyBreakOption.Early && !full)
                    return new StoreIntentResultDto { Ok = false, Error = "The piggy bank is not full yet.", StoreProductId = check.StoreProductId };
                if (option == Khela.Common.Piggy.PiggyBreakOption.Early && (state == null || state.Amount <= 0m || full))
                    return new StoreIntentResultDto { Ok = false, Error = full ? "The bank is already full — take the full offer." : "There is nothing in the piggy bank yet.", StoreProductId = check.StoreProductId };
            }

            var intentId = Guid.NewGuid().ToString("N");
            try { await _redis.GetDatabase().StringSetAsync($"khela:store:intent:{intentId}", $"{userId:N}|{p.Id}|{platform}", TimeSpan.FromHours(2)); } catch { /* funnel marker only */ }
            return new StoreIntentResultDto { Ok = true, StoreProductId = check.StoreProductId, IntentId = intentId };
        }

        // ------------------------------------------------------------------ redeem

        public async Task<RedeemPurchaseResultDto> RedeemAsync(Guid userId, RedeemPurchaseRequest req, CancellationToken ct = default)
        {
            if (req == null) return Fail(RedeemStatus.Error, "Missing request.");
            if (req.Platform == StorePlatform.Unknown) return Fail(RedeemStatus.Error, "Missing platform.");
            var cfg = await _catalog.GetConfigAsync();

            // kill switches
            if (!cfg.Enabled || !await StoreSwitches.StoreEnabledAsync(_redis, _config))
                return Fail(RedeemStatus.StoreDisabled, "The store is closed right now.");
            var verifier = _verifiers.Resolve(req.Platform);
            if (verifier == null || !await StoreSwitches.PlatformEnabledAsync(_redis, _config, req.Platform))
                return Fail(RedeemStatus.PlatformDisabled, "Purchases are not available on this platform yet.");
            if (!verifier.IsConfigured)
                return Fail(RedeemStatus.PlatformDisabled, "This platform is not configured on the server yet.", transient: true);

            // the key — before anything else
            if (!verifier.TryExtractTransactionKey(req, out var txnKey, out var keyError))
                return Fail(RedeemStatus.Invalid, keyError ?? "Unreadable receipt.");

            // product (hint) — the verified store product wins later
            var product = cfg.Find(req.ProductId) ?? StoreCatalog.ResolveByStoreId(cfg, req.Platform, req.StoreProductId);
            var now = DateTime.UtcNow;

            // RESERVE — the unique (Platform, StoreTransactionId) index is the mutex
            var row = await _db.StorePurchases.FirstOrDefaultAsync(s => s.Platform == req.Platform && s.StoreTransactionId == txnKey, ct);
            if (row == null)
            {
                row = new StorePurchase
                {
                    UserId = userId,
                    Platform = req.Platform,
                    ProductId = product?.Id ?? Clamp(req.ProductId ?? req.StoreProductId ?? "unknown", 64),
                    StoreProductId = Clamp(req.StoreProductId ?? (product != null ? StoreCatalog.StoreIdFor(product, req.Platform) : null) ?? req.ProductId ?? "unknown", 128),
                    StoreTransactionId = txnKey,
                    StoreOrderId = Clamp(req.TransactionId, 128),
                    ProductType = product?.ProductType ?? StoreProductType.Consumable,
                    Status = StorePurchaseStatus.Pending,
                    UsdReference = product?.UsdReference ?? 0m,
                    CatalogSnapshotJson = product == null ? null : JsonSerializer.Serialize(product, StoreCatalog.JsonOptions),
                    RawReceipt = StoreMath.Cap(req.Platform == StorePlatform.AppStore ? req.Jws : req.Receipt, _options.CurrentValue.MaxReceiptBytes),
                    ClientPriceMicros = req.ClientPriceMicros,
                    ClientPriceCurrency = Clamp(req.ClientPriceCurrency, 8),
                    ClientPurchaseId = Clamp(req.ClientPurchaseId, 64),
                    ClientVersion = Clamp(req.ClientVersion, 32),
                    CountryCode = Clamp(await _db.Users.AsNoTracking().Where(u => u.Id == userId.ToString()).Select(u => u.CountryCode).FirstOrDefaultAsync(ct), 8),
                    CreatedAt = now, UpdatedAt = now,
                };
                _db.StorePurchases.Add(row);
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateException)
                {
                    // Lost the race to a concurrent redeem of the same receipt: drive the winner's row instead.
                    _db.ChangeTracker.Clear();
                    row = await _db.StorePurchases.FirstOrDefaultAsync(s => s.Platform == req.Platform && s.StoreTransactionId == txnKey, ct);
                    if (row == null) return Fail(RedeemStatus.Error, "Could not reserve the purchase; try again.", transient: true);
                }
            }

            if (row.UserId != userId)
            {
                // The same store transaction redeemed by a different account — a shared-device/refund-abuse signal. The money
                // belongs to whoever paid; we neither re-grant nor reveal: answer as a replay to the original owner's outcome.
                _logger.LogWarning("Store purchase {Id} ({Platform}/{Txn}) re-submitted by a different user {Other}; owner {Owner}.", row.Id, row.Platform, Tail(txnKey), userId, row.UserId);
                return Fail(RedeemStatus.Invalid, "This purchase belongs to another account.");
            }

            return await DriveAsync(row, req, verifier, cfg, ct);
        }

        /// <summary>Verify (if needed) → grant (if needed) → complete. Everything here is idempotent; a throw leaves the row re-drivable.</summary>
        private async Task<RedeemPurchaseResultDto> DriveAsync(StorePurchase row, RedeemPurchaseRequest req, IStoreReceiptVerifier verifier, StoreCatalogConfig cfg, CancellationToken ct)
        {
            var o = _options.CurrentValue;

            switch (row.Status)
            {
                case StorePurchaseStatus.Granted when row.CompletedAt != null:
                    return await AlreadyGrantedAsync(row, ct);
                case StorePurchaseStatus.Invalid:
                    return Fail(RedeemStatus.Invalid, row.LastError ?? "The store rejected this purchase.", row);
                case StorePurchaseStatus.Refunded:
                case StorePurchaseStatus.Revoked:
                case StorePurchaseStatus.Expired:
                    return Fail(RedeemStatus.Invalid, "This purchase was refunded or revoked.", row);
            }

            // ---- VERIFY ----
            ReceiptVerification verification = null;
            if (row.Status == StorePurchaseStatus.Pending)
            {
                verification = await verifier.VerifyAsync(req, row.ProductType, ct);
                row.Attempts++;
                row.UpdatedAt = DateTime.UtcNow;
                switch (verification.Outcome)
                {
                    case VerifyOutcome.Transient:
                        row.LastError = Clamp(verification.Reason, 500);
                        await SaveQuietlyAsync(ct);
                        return Fail(RedeemStatus.Error, verification.Reason, row, transient: true);
                    case VerifyOutcome.PendingPayment:
                        row.LastError = Clamp(verification.Reason, 500);
                        row.VerifierJson = verification.RawJson;
                        await SaveQuietlyAsync(ct);
                        return Fail(RedeemStatus.Pending, verification.Reason ?? "Payment pending.", row);
                    case VerifyOutcome.Invalid:
                        row.Status = StorePurchaseStatus.Invalid;
                        row.LastError = Clamp(verification.Reason, 500);
                        row.VerifierJson = verification.RawJson;
                        await SaveQuietlyAsync(ct);
                        _logger.LogWarning("Store purchase {Id} ({Platform}) INVALID: {Reason}", row.Id, row.Platform, verification.Reason);
                        return Fail(RedeemStatus.Invalid, verification.Reason ?? "The store rejected this purchase.", row);
                }

                // Valid — the verified store product is the truth; the client's ProductId was only a hint.
                var applyFail = await ApplyVerificationAsync(row, verification, cfg, ct);
                if (applyFail != null) return applyFail;
            }

            // ---- GRANT + COMPLETE ----
            return await GrantAsync(row, verification, ct);
        }

        /// <summary>
        /// Stamp a VALID verification onto the reserved row: resolve the product the store actually sold (the catalog snapshot
        /// is taken here if the hint was wrong or missing), copy the store facts, mark Verified. Rail-agnostic — the same for a
        /// Play receipt, an Apple JWS, a fake-store order or a web-checkout confirmation. Returns a failure result when the
        /// sold product is unknown to the catalog (evidence kept; admin re-drives after adding it), else null.
        /// </summary>
        private async Task<RedeemPurchaseResultDto> ApplyVerificationAsync(StorePurchase row, ReceiptVerification verification, StoreCatalogConfig cfg, CancellationToken ct,
            StoreProductDef trustedProduct = null)
        {
            // ONLY the store decides what was bought. There is deliberately NO fallback to a CLIENT-supplied ProductId hint:
            // both ProductId and StoreProductId on a client-reserved row are copied verbatim from the request, and the client
            // can read its own receipt's store product id — so a hint-based fallback would let a player who bought the $0.99
            // pack name the $99.99 one and be granted it (plus $99.99 of VIP/Loyalty/XP) whenever a SKU is momentarily
            // unmapped in the catalog. An unmapped SKU is an operations problem: the evidence is kept below, an admin maps it
            // in the catalog, and the purchase is re-driven.
            //
            // <paramref name="trustedProduct"/> is the ONE exception, and it is not a client value: RedeemVerifiedAsync is
            // called by SERVER-SIDE adapter code (our own web checkout / a payment webhook) which names the product itself.
            // Those rails have no third-party store SKU to resolve against, so the adapter's product is the truth there.
            var verifiedProduct = StoreCatalog.ResolveByStoreId(cfg, row.Platform, verification.StoreProductId) ?? trustedProduct;
            if (verifiedProduct == null)
            {
                // Paid for something our catalog doesn't know (not yet authored?). Keep the evidence; an admin adds the product and re-drives.
                row.LastError = Clamp($"Unknown store product '{verification.StoreProductId}' on {row.Platform} — add it to the catalog and re-drive.", 500);
                row.VerifierJson = verification.RawJson;
                row.StoreProductId = Clamp(verification.StoreProductId ?? row.StoreProductId, 128);
                await SaveQuietlyAsync(ct);
                _logger.LogError("Store purchase {Id}: verified store product {Store} is not in the catalog.", row.Id, verification.StoreProductId);
                return Fail(RedeemStatus.ProductUnavailable, "This product is not available right now.", row, transient: true);
            }
            if (!string.Equals(verifiedProduct.Id, row.ProductId, StringComparison.OrdinalIgnoreCase) || row.CatalogSnapshotJson == null)
            {
                row.ProductId = verifiedProduct.Id;
                row.ProductType = verifiedProduct.ProductType;
                row.UsdReference = verifiedProduct.UsdReference;
                row.CatalogSnapshotJson = JsonSerializer.Serialize(verifiedProduct, StoreCatalog.JsonOptions);
            }
            row.StoreProductId = Clamp(verification.StoreProductId ?? row.StoreProductId, 128);
            row.StoreOrderId = Clamp(verification.StoreOrderId ?? row.StoreOrderId, 128);
            row.OriginalTransactionId = Clamp(verification.OriginalTransactionId, 128);
            row.IsTest = verification.IsTest;
            row.Environment = Clamp(verification.Environment, 16);
            row.RegionCode = Clamp(verification.RegionCode, 8);
            row.ObfuscatedAccountId = Clamp(verification.ObfuscatedAccountId, 64);
            if (verification.PriceMicros.HasValue && row.ClientPriceMicros == null) { row.ClientPriceMicros = verification.PriceMicros; row.ClientPriceCurrency = Clamp(verification.PriceCurrency, 8); }
            row.SubscriptionExpiresAt = verification.SubscriptionExpiresUtc;
            row.VerifierJson = verification.RawJson;
            row.VerifiedAt = DateTime.UtcNow;
            row.Status = StorePurchaseStatus.Verified;
            row.LastError = null;
            if (verification.Acknowledged == true) row.AcknowledgedAt ??= DateTime.UtcNow;

            // Persist the QUANTITY the store reported, now, while we still have the verification. A grant interrupted after
            // this point is re-driven WITHOUT re-verifying (the row is no longer Pending), so a quantity kept only in the
            // verification object would fall back to 1 on the retry and under-deliver a paid purchase.
            // Same for the STORE's purchase time: it decides which sale a value-bonus purchase was bought under (the receipt may
            // reach us hours after the payment — pending/cash, an app killed before the redeem, a restore). Persisted once, here,
            // so every later drive judges the same instant.
            if (verification.Quantity > 1 || verification.PurchaseTimeUtc.HasValue)
            {
                var f = ReadFulfilment(row);
                bool changed = false;
                if (f.Quantity < verification.Quantity) { f.Quantity = verification.Quantity; changed = true; }
                if (!f.PurchasedAtUtc.HasValue && verification.PurchaseTimeUtc.HasValue) { f.PurchasedAtUtc = verification.PurchaseTimeUtc; changed = true; }
                if (changed) row.FulfilmentJson = JsonSerializer.Serialize(f, StoreCatalog.JsonOptions);
            }
            await SaveQuietlyAsync(ct);
            return null;
        }

        /// <summary>The purchase's fulfilment record, or a fresh one. Never null.</summary>
        private static StoreFulfilment ReadFulfilment(StorePurchase row)
        {
            if (string.IsNullOrWhiteSpace(row.FulfilmentJson)) return new StoreFulfilment();
            try { return JsonSerializer.Deserialize<StoreFulfilment>(row.FulfilmentJson, StoreCatalog.JsonOptions) ?? new StoreFulfilment(); }
            catch { return new StoreFulfilment(); }
        }

        public async Task<RedeemPurchaseResultDto> RedeemVerifiedAsync(Guid userId, StorePlatform platform, string productId, ReceiptVerification verified,
            string rawEvidence = null, string clientPurchaseId = null, string clientVersion = null, CancellationToken ct = default)
        {
            if (verified == null) return Fail(RedeemStatus.Error, "Missing verification.");
            if (platform == StorePlatform.Unknown) return Fail(RedeemStatus.Error, "Missing platform.");
            if (verified.Outcome != VerifyOutcome.Valid) return Fail(verified.Outcome == VerifyOutcome.PendingPayment ? RedeemStatus.Pending : verified.Outcome == VerifyOutcome.Transient ? RedeemStatus.Error : RedeemStatus.Invalid, verified.Reason ?? "Not a valid purchase.", transient: verified.Outcome == VerifyOutcome.Transient);
            if (string.IsNullOrWhiteSpace(verified.StoreTransactionId)) return Fail(RedeemStatus.Error, "A verified purchase needs a transaction id.");
            var cfg = await _catalog.GetConfigAsync();
            if (!cfg.Enabled || !await StoreSwitches.StoreEnabledAsync(_redis, _config)) return Fail(RedeemStatus.StoreDisabled, "The store is closed right now.");
            if (!await StoreSwitches.PlatformEnabledAsync(_redis, _config, platform)) return Fail(RedeemStatus.PlatformDisabled, "Purchases are not available on this platform yet.");

            var product = cfg.Find(productId) ?? StoreCatalog.ResolveByStoreId(cfg, platform, verified.StoreProductId);
            var txnKey = Clamp(verified.StoreTransactionId.Trim(), 256);
            var now = DateTime.UtcNow;
            var row = await _db.StorePurchases.FirstOrDefaultAsync(s => s.Platform == platform && s.StoreTransactionId == txnKey, ct);
            if (row == null)
            {
                row = new StorePurchase
                {
                    UserId = userId, Platform = platform,
                    ProductId = product?.Id ?? Clamp(productId ?? verified.StoreProductId ?? "unknown", 64),
                    StoreProductId = Clamp(verified.StoreProductId ?? (product != null ? StoreCatalog.StoreIdFor(product, platform) : null) ?? productId ?? "unknown", 128),
                    StoreTransactionId = txnKey, StoreOrderId = Clamp(verified.StoreOrderId, 128),
                    ProductType = product?.ProductType ?? (verified.IsSubscription ? StoreProductType.Subscription : StoreProductType.Consumable),
                    Status = StorePurchaseStatus.Pending, UsdReference = product?.UsdReference ?? 0m,
                    CatalogSnapshotJson = product == null ? null : JsonSerializer.Serialize(product, StoreCatalog.JsonOptions),
                    RawReceipt = StoreMath.Cap(rawEvidence ?? verified.RawJson, _options.CurrentValue.MaxReceiptBytes),
                    ClientPurchaseId = Clamp(clientPurchaseId, 64), ClientVersion = Clamp(clientVersion, 32),
                    CountryCode = Clamp(await _db.Users.AsNoTracking().Where(u => u.Id == userId.ToString()).Select(u => u.CountryCode).FirstOrDefaultAsync(ct), 8),
                    CreatedAt = now, UpdatedAt = now,
                };
                _db.StorePurchases.Add(row);
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateException)
                {
                    _db.ChangeTracker.Clear();
                    row = await _db.StorePurchases.FirstOrDefaultAsync(s => s.Platform == platform && s.StoreTransactionId == txnKey, ct);
                    if (row == null) return Fail(RedeemStatus.Error, "Could not reserve the purchase; try again.", transient: true);
                }
            }
            if (row.UserId != userId) return Fail(RedeemStatus.Invalid, "This purchase belongs to another account.");

            switch (row.Status)
            {
                case StorePurchaseStatus.Granted when row.CompletedAt != null: return await AlreadyGrantedAsync(row, ct);
                case StorePurchaseStatus.Invalid: return Fail(RedeemStatus.Invalid, row.LastError ?? "The purchase was rejected.", row);
                case StorePurchaseStatus.Refunded: case StorePurchaseStatus.Revoked: case StorePurchaseStatus.Expired:
                    return Fail(RedeemStatus.Invalid, "This purchase was refunded or revoked.", row);
            }
            if (row.Status == StorePurchaseStatus.Pending)
            {
                row.Attempts++;
                // `product` came from this server-side adapter call, not from a game client — safe to trust when the rail
                // has no third-party SKU to resolve against (see ApplyVerificationAsync).
                var applyFail = await ApplyVerificationAsync(row, verified, cfg, ct, trustedProduct: product);
                if (applyFail != null) return applyFail;
            }
            return await GrantAsync(row, verified, ct);
        }

        private async Task<RedeemPurchaseResultDto> GrantAsync(StorePurchase row, ReceiptVerification verification, CancellationToken ct)
        {
            var o = _options.CurrentValue;
            var product = ProductFromRow(row);
            if (product == null) return Fail(RedeemStatus.ProductUnavailable, "This product is not available right now.", row, transient: true);

            var fulfilment = ReadFulfilment(row);

            // QUANTITY comes from the PERSISTED fulfilment record, never from `verification` alone: on a re-drive of an
            // interrupted grant the row is already Verified, so `verification` is null and a multi-quantity purchase
            // (Google sells consumables in quantities) would be paid out once instead of N times.
            if (verification != null && verification.Quantity > fulfilment.Quantity) fulfilment.Quantity = verification.Quantity;
            int quantity = Math.Max(1, fulfilment.Quantity);

            var ctx = new StoreGrantContext
            {
                UserId = row.UserId,
                Purchase = row,
                Product = product,
                Verification = verification,
                CreditType = TransactionType.PaidPurchase,
                IdemRoot = StoreMath.IdemRoot(row.Id),
                ExternalRef = StoreMath.ExternalRef(row.Platform, row.StoreTransactionId, row.Id),
                Description = $"Store {product.Id}" + (row.IsTest ? " (test)" : ""),
            };

            // Account binding: a signal, never a gate (the money was paid).
            if (verification != null && !string.IsNullOrWhiteSpace(verification.ObfuscatedAccountId))
            {
                var expected = row.Platform == StorePlatform.AppStore ? row.UserId.ToString("D") : StoreMath.AccountHash(row.UserId);
                if (!string.Equals(verification.ObfuscatedAccountId, expected, StringComparison.OrdinalIgnoreCase) && !fulfilment.Flags.Any(f => f.StartsWith("account-binding")))
                    fulfilment.Flags.Add("account-binding: store account id does not match this user");
            }
            // Eligibility at redeem: flag, never refuse (principle 4).
            var avail = await _catalog.CheckAsync(row.Platform, row.UserId, product.Id);
            if (!avail.Ok && !fulfilment.Flags.Any(f => f.StartsWith("eligibility")))
                fulfilment.Flags.Add("eligibility: " + avail.Reason);

            try
            {
                // 1. plain reward lines — currency straight through the wallet as PaidPurchase; other kinds via the reward granters
                var lines = product.Lines ?? new List<RewardGrant>();

                // A VALUE-BONUS sale pays from the purchase's SNAPSHOT, judged at the instant the STORE says it was bought (the
                // verifier's purchase time, persisted on the row; the reserve time when the store gave none) + grace: what the
                // card promised when it was tapped, whatever the catalog says now and however late the receipt arrives —
                // a pending/cash payment, an app killed before the redeem, a restore. Deterministic on (snapshot, persisted
                // time), so a re-drive pays the same amounts under the same line keys; a refund reverses the persisted
                // (boosted) lines. `product` IS the snapshot (ProductFromRow), never the live catalog.
                var decidedAt = StoreCatalog.SaleDecisionTime(row.CreatedAt, fulfilment.PurchasedAtUtc);
                var sale = StoreCatalog.ActiveSale(product, decidedAt, grace: true);
                if (sale != null && sale.Kind == StoreSaleKind.ValueBonus)
                {
                    lines = StoreSaleMath.Apply(lines, sale.Percent);
                    fulfilment.Sale ??= $"ValueBonus +{sale.Percent}%" + (string.IsNullOrWhiteSpace(sale.Label) ? "" : $" ({sale.Label.Trim()})");
                }
                else if (fulfilment.Sale == null)
                {
                    // A PriceOff SKU grants its own (identical) lines; only the record needs to say why this SKU exists.
                    try
                    {
                        var regular = StoreCatalog.RegularFor(await _catalog.GetConfigAsync(), product.Id);
                        if (regular != null) fulfilment.Sale = $"PriceOff SKU of {regular.Id}";
                    }
                    catch { /* informational only */ }
                }

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    if (line == null) continue;
                    var key = StoreMath.LineKey(row.Id, i);
                    if (fulfilment.Lines.Any(l => l.CorrelationId == key)) continue;   // already paid on a previous drive
                    if (line.Kind == RewardKind.Currency)
                    {
                        if (!RewardCurrencies.TryParseAllowed(line.Id, out var currency) || line.Amount <= 0m)
                        {
                            fulfilment.Flags.Add($"line {i}: refused ({line.Id} {line.Amount})");
                            continue;
                        }
                        var amount = line.Amount * quantity;
                        var txn = await _wallet.CreditAsync(row.UserId.ToString(), currency, amount, ctx.CreditType, key, new WalletContext
                        {
                            Description = ctx.Description,
                            ExternalRef = ctx.ExternalRef,
                            MetadataJson = JsonSerializer.Serialize(new { store = row.Platform.ToString(), product = product.Id, storeProduct = row.StoreProductId, usd = row.UsdReference, test = row.IsTest, purchase = row.Id }),
                        });
                        fulfilment.Lines.Add(new StoreFulfilledLine { Kind = (int)RewardKind.Currency, Id = currency.ToString(), Amount = txn?.Amount ?? amount, CorrelationId = key, Balance = txn?.BalanceAfter ?? 0m });
                    }
                    else
                    {
                        var applied = await _rewards.GrantOneAsync(row.UserId, line, key, ctx.Description, ctx.ExternalRef);
                        foreach (var a in applied ?? new List<GrantedLineDto>())
                            fulfilment.Lines.Add(new StoreFulfilledLine { Kind = a.Kind, Id = a.Id, Amount = a.Amount, CorrelationId = key, Balance = a.Balance });
                        if (applied == null || applied.Count == 0) fulfilment.Flags.Add($"line {i}: {line.Kind} {line.Id} not granted (no granter / invalid)");
                    }
                }

                // 2. the effect
                if (product.Effect != null && !string.IsNullOrWhiteSpace(product.Effect.Type) && fulfilment.EffectType == null)
                {
                    // An effect runs ONCE however many units were bought (a piggy bank cannot be broken three times by one
                    // order). Flag it so an admin can comp the difference rather than the player silently losing units.
                    if (quantity > 1) fulfilment.Flags.Add($"quantity {quantity}: the effect '{product.Effect.Type}' was applied once — comp the remainder by hand");
                    if (!_handlers.TryGetValue(product.Effect.Type.Trim(), out var handler))
                        throw new InvalidOperationException($"No grant handler for effect '{product.Effect.Type}'.");
                    await handler.GrantAsync(ctx, fulfilment, ct);
                    fulfilment.EffectType = product.Effect.Type;
                    fulfilment.EffectArg = product.Effect.Arg;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leave the row re-drivable (Verified/Granted-incomplete) with whatever DID get paid recorded — the keys make the retry exact.
                row.Attempts++;
                row.LastError = Clamp("grant: " + ex.Message, 500);
                row.FulfilmentJson = JsonSerializer.Serialize(fulfilment, StoreCatalog.JsonOptions);
                row.UpdatedAt = DateTime.UtcNow;
                await SaveQuietlyAsync(ct);
                _logger.LogError(ex, "Store purchase {Id} grant failed after {Lines} lines; will be re-driven.", row.Id, fulfilment.Lines.Count);
                return Fail(RedeemStatus.Error, "Fulfilment failed; it will be retried.", row, transient: true);
            }

            // 3. COMPLETE
            fulfilment.CompletedAtUtc = DateTime.UtcNow;
            row.Status = StorePurchaseStatus.Granted;
            row.GrantedAt ??= DateTime.UtcNow;
            row.CompletedAt = DateTime.UtcNow;
            row.FulfilmentJson = JsonSerializer.Serialize(fulfilment, StoreCatalog.JsonOptions);
            row.LastError = null;
            row.UpdatedAt = DateTime.UtcNow;
            await SaveQuietlyAsync(ct);

            // 4. acknowledge (Google) — best effort; the reconciler is the backstop
            if (row.Platform == StorePlatform.GooglePlay && o.GooglePlay.AcknowledgeOnGrant && row.AcknowledgedAt == null)
            {
                var verifier = _verifiers.Resolve(row.Platform);
                if (verifier != null && await verifier.AcknowledgeAsync(row, ct)) { row.AcknowledgedAt = DateTime.UtcNow; await SaveQuietlyAsync(ct); }
            }

            // 5. spend hooks — best-effort, idempotent, skipped for test purchases unless configured (overlay-aware switch)
            if (!row.IsTest || await StoreSwitches.BoolAsync(_redis, _config, "Store:TestPurchasesFeedSpend", o.TestPurchasesFeedSpend))
                await HooksAsync(row, ct);

            _logger.LogInformation("StorePurchaseGranted {UserId} {ProductId} {Platform} usd {Usd} test {IsTest} purchase {Id}",
                row.UserId, row.ProductId, row.Platform, row.UsdReference, row.IsTest, row.Id);

            return await ResultAsync(row, RedeemStatus.Granted, fulfilment, ct);
        }

        private async Task HooksAsync(StorePurchase row, CancellationToken ct)
        {
            var o = _options.CurrentValue;
            var root = StoreMath.IdemRoot(row.Id);
            try { if (row.UsdReference > 0m) await _vip.RecordPurchaseAsync(row.UserId, row.UsdReference, root); }
            catch (Exception ex) { _logger.LogWarning(ex, "VIP purchase hook failed for {Id}", row.Id); }
            try { if (row.UsdReference > 0m) await _loyalty.RecordPurchaseAsync(row.UserId, row.UsdReference, "iaplp:" + row.Id.ToString("N")); }
            catch (Exception ex) { _logger.LogWarning(ex, "Loyalty purchase hook failed for {Id}", row.Id); }
            try
            {
                var xpPerUsd = await StoreSwitches.DecimalAsync(_redis, _config, "Store:XpPerUsd", o.XpPerUsd);
                var xp = (long)Math.Floor(row.UsdReference * xpPerUsd);
                if (xp > 0) await _progression.GrantXpAsync(row.UserId, xp, "store", "iapxp:" + row.Id.ToString("N"), bypassDailyCap: true);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "XP purchase hook failed for {Id}", row.Id); }
        }

        // ------------------------------------------------------------------ restore / history / redrive / refund

        public async Task<StoreRestoreResultDto> RestoreAsync(Guid userId, StoreRestoreRequest req, CancellationToken ct = default)
        {
            var results = new List<RedeemPurchaseResultDto>();
            foreach (var item in req?.Items ?? new List<RedeemPurchaseRequest>())
            {
                if (item == null) continue;
                if (item.Platform == StorePlatform.Unknown) item.Platform = req.Platform;
                results.Add(await RedeemAsync(userId, item, ct));
            }
            return new StoreRestoreResultDto { Results = results };
        }

        public async Task<List<StorePurchaseDto>> GetHistoryAsync(Guid userId, int take = 50, CancellationToken ct = default)
        {
            take = Math.Clamp(take, 1, 200);
            return await _db.StorePurchases.AsNoTracking()
                .Where(s => s.UserId == userId)
                .OrderByDescending(s => s.CreatedAt).Take(take)
                .Select(s => new StorePurchaseDto
                {
                    PurchaseId = s.Id, ProductId = s.ProductId, Platform = s.Platform, Status = s.Status.ToString(),
                    UsdReference = s.UsdReference, IsTest = s.IsTest, CreatedAtUtc = s.CreatedAt, GrantedAtUtc = s.GrantedAt,
                }).ToListAsync(ct);
        }

        public async Task<RedeemPurchaseResultDto> RedriveAsync(Guid purchaseId, CancellationToken ct = default)
        {
            var row = await _db.StorePurchases.FirstOrDefaultAsync(s => s.Id == purchaseId, ct);
            if (row == null) return Fail(RedeemStatus.Error, "No such purchase.");
            var verifier = _verifiers.Resolve(row.Platform);
            if (verifier == null) return Fail(RedeemStatus.PlatformDisabled, "No verifier for this platform.", row);
            var cfg = await _catalog.GetConfigAsync();
            var req = new RedeemPurchaseRequest
            {
                Platform = row.Platform, ProductId = row.ProductId, StoreProductId = row.StoreProductId, TransactionId = row.StoreOrderId,
                Receipt = row.Platform == StorePlatform.AppStore ? null : row.RawReceipt,
                Jws = row.Platform == StorePlatform.AppStore ? row.RawReceipt : null,
                ClientPriceMicros = row.ClientPriceMicros, ClientPriceCurrency = row.ClientPriceCurrency,
            };
            // An admin re-drive of an Invalid row (after a config fix) is allowed: reset it to Pending first.
            if (row.Status == StorePurchaseStatus.Invalid) { row.Status = StorePurchaseStatus.Pending; row.LastError = null; await SaveQuietlyAsync(ct); }
            return await DriveAsync(row, req, verifier, cfg, ct);
        }

        public async Task<bool> MarkRefundedAsync(Guid purchaseId, string source, string reason, bool partial = false, CancellationToken ct = default)
        {
            var row = await _db.StorePurchases.FirstOrDefaultAsync(s => s.Id == purchaseId, ct);
            if (row == null) return false;
            if (row.Status == StorePurchaseStatus.Refunded || row.Status == StorePurchaseStatus.Revoked) return true;   // idempotent

            var fulfilment = ReadFulfilment(row);
            var wasGranted = row.Status == StorePurchaseStatus.Granted;

            // The status is written LAST, deliberately. The guard above short-circuits on Status == Refunded, so committing
            // it first would make a refund that dies mid-reversal permanently "handled" with the money never taken back:
            // every retry source (webhook re-delivery, the admin queue, the voided poll) would see Refunded and return true.
            // Doing the reversal first keeps the row re-drivable — RollbackAsync and RevokeGoldenAsync are both idempotent,
            // so re-running costs nothing.
            if (wasGranted)
            {
                // Subscription → the golden window closes (collected rewards are never clawed back — PASS_SPEC §5.4).
                if (row.ProductType == StoreProductType.Subscription)
                {
                    try
                    {
                        var product = ProductFromRow(row);
                        var passKey = string.IsNullOrWhiteSpace(product?.Effect?.Arg) ? PassCatalog.MonthlyKey : product.Effect.Arg;
                        // Revoke EVERY window of this pass, not one purchaseRef. Each renewal was recorded under its own key
                        // (the initial grant uses the bare order id; a renewal appends its expiry hour), and a renewal also
                        // moves row.StoreOrderId — so a single-key revoke matches nothing once the subscription has renewed
                        // even once, leaving a refunded player golden for the rest of the window. A player cannot hold two
                        // concurrent subscriptions to the same pass, so "all windows of this pass" is the right scope.
                        await _pass.RevokeGoldenAsync(row.UserId, passKey, null, source ?? "refund");
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Golden revoke failed for refunded purchase {Id}", row.Id); }
                }

                // Currency lines → policy (Store:Refunds:Policy, overlay-aware). Rollback = the wallet's own reversal (compensating
                // Refund row, never negative); if the chips were already spent it throws → Flag. Piggy/VIP effects are never unwound.
                var policy = await StoreSwitches.StringAsync(_redis, _config, "Store:Refunds:Policy", _options.CurrentValue.Refunds.Policy);
                if (partial)
                {
                    // A PARTIAL (quantity-based) refund: the store took back some units, not the purchase. We cannot reverse a
                    // fraction — the wallet reverses a whole correlation id, and re-keying a spent id is forbidden — so we
                    // never claw back here. Over-flagging costs an admin a minute; over-clawing takes chips the player still
                    // paid for and cannot be undone.
                    fulfilment.Flags.Add($"PARTIAL refund ({reason}): balances untouched — comp the delta by hand");
                    _logger.LogWarning("Store purchase {Id}: PARTIAL refund recorded, balances untouched ({Reason})", row.Id, reason);
                }
                else if (string.Equals(policy?.Trim(), "Rollback", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in fulfilment.Lines.Where(l => l.Kind == (int)RewardKind.Currency && !string.IsNullOrEmpty(l.CorrelationId)))
                    {
                        if (!RewardCurrencies.TryParse(line.Id, out var currency)) continue;
                        try
                        {
                            var reversal = await _wallet.RollbackAsync(row.UserId.ToString(), currency, line.CorrelationId,
                                new WalletContext { Description = "Store refund " + row.ProductId, ExternalRef = StoreMath.ExternalRef(row.Platform, row.StoreTransactionId, row.Id) });
                            fulfilment.Flags.Add(reversal != null ? $"refund: reversed {line.Amount} {currency}" : $"refund: nothing to reverse for {line.CorrelationId}");
                        }
                        catch (InsufficientFundsException)
                        {
                            fulfilment.Flags.Add($"refund: {line.Amount} {currency} already spent — flagged, not reversed");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Refund rollback failed for {Id} line {Key}", row.Id, line.CorrelationId);
                            fulfilment.Flags.Add($"refund: rollback error for {line.CorrelationId}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    fulfilment.Flags.Add("refund: policy Flag — balances untouched");
                }
                row.FulfilmentJson = JsonSerializer.Serialize(fulfilment, StoreCatalog.JsonOptions);
            }

            // Only now is the refund recorded as handled.
            row.Status = StorePurchaseStatus.Refunded;
            row.RefundedAt = DateTime.UtcNow;
            row.RefundSource = Clamp(source, 32);
            row.LastError = Clamp((partial ? "partial refund: " : "refund: ") + (reason ?? source), 500);
            row.UpdatedAt = DateTime.UtcNow;
            await SaveQuietlyAsync(ct);

            _logger.LogWarning("StorePurchaseRefunded {UserId} {ProductId} {Platform} usd {Usd} source {Source} purchase {Id}",
                row.UserId, row.ProductId, row.Platform, row.UsdReference, source, row.Id);
            return true;
        }

        // ------------------------------------------------------------------ lookups + subscription lifecycle (webhooks / reconciler)

        public Task<StorePurchase> FindAsync(StorePlatform platform, string storeTransactionId, CancellationToken ct = default)
            => string.IsNullOrWhiteSpace(storeTransactionId)
                ? Task.FromResult<StorePurchase>(null)
                : _db.StorePurchases.FirstOrDefaultAsync(s => s.Platform == platform && s.StoreTransactionId == storeTransactionId, ct);

        public Task<StorePurchase> FindByOriginalTransactionAsync(StorePlatform platform, string originalTransactionId, CancellationToken ct = default)
            => string.IsNullOrWhiteSpace(originalTransactionId)
                ? Task.FromResult<StorePurchase>(null)
                : _db.StorePurchases.Where(s => s.Platform == platform && s.ProductType == StoreProductType.Subscription
                                             && (s.OriginalTransactionId == originalTransactionId || s.StoreTransactionId == originalTransactionId || s.StoreOrderId == originalTransactionId))
                    .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);

        public async Task<SubscriptionUpdate> ApplySubscriptionUpdateAsync(StorePurchase row, ReceiptVerification fresh, string source, CancellationToken ct = default)
        {
            if (row == null || fresh == null) return SubscriptionUpdate.NoChange;
            if (fresh.Outcome == VerifyOutcome.Transient || fresh.Outcome == VerifyOutcome.PendingPayment) return SubscriptionUpdate.NoChange;
            var now = DateTime.UtcNow;

            // Revoked / refunded by the store → close the window (collected rewards are never clawed back).
            if (fresh.Revoked || fresh.Outcome == VerifyOutcome.Invalid)
            {
                if (row.Status == StorePurchaseStatus.Granted)
                {
                    await MarkRefundedAsync(row.Id, source, fresh.Reason ?? "revoked by the store", ct: ct);
                    return SubscriptionUpdate.Revoked;
                }
                return SubscriptionUpdate.NoChange;
            }

            // Renewed: the store's new expiry is later than the window we know → append the next window (idempotent on purchaseRef).
            if (fresh.SubscriptionExpiresUtc.HasValue && (row.SubscriptionExpiresAt == null || fresh.SubscriptionExpiresUtc > row.SubscriptionExpiresAt.Value.AddMinutes(1)))
            {
                var product = ProductFromRow(row);
                var passKey = string.IsNullOrWhiteSpace(product?.Effect?.Arg) ? PassCatalog.MonthlyKey : product.Effect.Arg;
                var startsAt = row.SubscriptionExpiresAt ?? fresh.SubscriptionStartUtc ?? now;
                var purchaseRef = StoreMath.FitOrHash((fresh.StoreOrderId ?? row.StoreOrderId ?? row.StoreTransactionId) + ":" + fresh.SubscriptionExpiresUtc.Value.ToString("yyyyMMddHH"), 96);
                var originalId = StoreMath.FitOrHash(row.OriginalTransactionId ?? row.StoreTransactionId, 96);
                if (_pass != null)
                {
                    var result = await _pass.GrantGoldenAsync(row.UserId, passKey, "iap", purchaseRef, startsAt, fresh.SubscriptionExpiresUtc.Value, originalId, fresh.AutoRenew);
                    if (result == null || !result.Ok) throw new InvalidOperationException("Renewal window could not be recorded: " + (result?.Error ?? "no result"));
                }
                row.SubscriptionExpiresAt = fresh.SubscriptionExpiresUtc;
                if (!string.IsNullOrWhiteSpace(fresh.StoreOrderId)) row.StoreOrderId = Clamp(fresh.StoreOrderId, 128);
                if (row.Status == StorePurchaseStatus.Expired) row.Status = StorePurchaseStatus.Granted;   // resubscribed
                row.UpdatedAt = now;
                await SaveQuietlyAsync(ct);
                _logger.LogInformation("Store subscription {Id} extended to {Until} ({Source})", row.Id, fresh.SubscriptionExpiresUtc, source);
                return SubscriptionUpdate.Extended;
            }

            // Ran out and will not renew: the entitlement ends by itself (ExpiresAt); mark the row so sweeps stop re-checking it.
            if (fresh.SubscriptionExpiresUtc.HasValue && fresh.SubscriptionExpiresUtc <= now && !fresh.AutoRenew && row.Status == StorePurchaseStatus.Granted)
            {
                row.Status = StorePurchaseStatus.Expired; row.UpdatedAt = now;
                await SaveQuietlyAsync(ct);
                return SubscriptionUpdate.Expired;
            }
            return SubscriptionUpdate.NoChange;
        }

        // ------------------------------------------------------------------ helpers

        private StoreProductDef ProductFromRow(StorePurchase row)
        {
            if (!string.IsNullOrWhiteSpace(row.CatalogSnapshotJson))
            {
                try { var p = JsonSerializer.Deserialize<StoreProductDef>(row.CatalogSnapshotJson, StoreCatalog.JsonOptions); if (p != null) return p; } catch { }
            }
            return null;
        }

        private async Task<RedeemPurchaseResultDto> AlreadyGrantedAsync(StorePurchase row, CancellationToken ct)
        {
            var fulfilment = ReadFulfilment(row);
            return await ResultAsync(row, RedeemStatus.AlreadyGranted, fulfilment, ct);
        }

        private async Task<RedeemPurchaseResultDto> ResultAsync(StorePurchase row, RedeemStatus status, StoreFulfilment fulfilment, CancellationToken ct)
        {
            decimal chips = 0m, kash = 0m;
            try
            {
                var balances = await _wallet.GetBalancesAsync(row.UserId.ToString());
                balances.TryGetValue(CurrencyType.Chips, out chips);
                balances.TryGetValue(CurrencyType.Kash, out kash);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Balance read after store grant failed for {Id}", row.Id); }

            return new RedeemPurchaseResultDto
            {
                Ok = true,
                Status = status,
                PurchaseId = row.Id,
                ProductId = row.ProductId,
                Grants = fulfilment.Lines.Select(l => new GrantedLineDto { Kind = l.Kind, Id = l.Id, Amount = l.Amount, Balance = l.Balance }).ToList(),
                NewChipBalance = chips,
                NewKashBalance = kash,
                IsTest = row.IsTest,
                Piggy = fulfilment.Piggy,
                Pass = fulfilment.Pass,
            };
        }

        private static RedeemPurchaseResultDto Fail(RedeemStatus status, string error, StorePurchase row = null, bool transient = false)
            => new RedeemPurchaseResultDto { Ok = false, Status = status, Error = error, Transient = transient, PurchaseId = row?.Id, ProductId = row?.ProductId, Grants = new List<GrantedLineDto>() };

        private async Task SaveQuietlyAsync(CancellationToken ct)
        {
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException ex)
            {
                // Two drives of the same row raced (client retry + reconciler). The legs are idempotent; reload and let the caller's
                // outcome stand — the next drive sees the winner's state.
                _logger.LogWarning(ex, "Store purchase row concurrency conflict; reloading.");
                foreach (var entry in ex.Entries) await entry.ReloadAsync(ct);
            }
        }

        private static string Clamp(string s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));
        private static string Tail(string s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 8 ? s : s.Substring(s.Length - 8);
    }
}
