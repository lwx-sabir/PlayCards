using System;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Piggy;
using Khela.Common.Store;
using Khela.Game.Services.Pass;
using Khela.Game.Services.Piggy;
using Khela.Game.Services.Vip;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Store.Grants
{
    /// <summary>
    /// Piggy break bought through the store: the verified purchase pays the player's bank via
    /// <see cref="IPiggyService.BreakVerifiedAsync"/> (step 4 of PIGGY_BANK_SPEC). Idempotent on the store transaction id
    /// (<c>PiggyBreaks.PurchaseId</c> is unique). The catalog product carries the OPTION (Full / FullDouble / Early) and the
    /// tier it was sold for; the payout is whatever the bank holds — principle 4 (never under-deliver) is handled inside
    /// the piggy service for a bank that moved between the tap and the receipt.
    /// </summary>
    public sealed class PiggyBreakGrantHandler : IStoreGrantHandler
    {
        private readonly IPiggyService _piggy;
        private readonly ILogger<PiggyBreakGrantHandler> _logger;

        public PiggyBreakGrantHandler(IPiggyService piggy, ILogger<PiggyBreakGrantHandler> logger) { _piggy = piggy; _logger = logger; }

        public string Effect => StoreCatalog.EffectPiggyBreak;

        public async Task GrantAsync(StoreGrantContext ctx, StoreFulfilment fulfilment, CancellationToken ct)
        {
            if (!StoreCatalog.TryParsePiggyOption(ctx.Product?.Effect?.Arg, out var option))
                throw new InvalidOperationException($"Product {ctx.Product?.Id}: PiggyBreak effect has no valid option.");

            // The piggy's own idempotency key = the store transaction (≤ 256, its column's width).
            var purchaseId = StoreMath.FitOrHash(ctx.Purchase.Platform + ":" + ctx.Purchase.StoreTransactionId, 256);
            // The rung this product was PRICED for (from the purchase's catalog snapshot). The piggy service caps the
            // payout at that rung's capacity when it is applied to a bank of another rung — the store cannot tell
            // which bank a receipt lands on, so a cheap rung's product must not buy an expensive bank.
            var soldTier = StoreCatalog.PiggyTierOf(ctx.Product);
            var result = await _piggy.BreakVerifiedAsync(ctx.UserId, option, purchaseId, ctx.Product.Id, ctx.Purchase.Id, ctx.CreditType, soldTier);
            fulfilment.Piggy = result;
            if (result == null || !result.Ok)
                throw new InvalidOperationException("Piggy break could not be paid: " + (result?.Error ?? "no result"));
            if (result.Amount <= 0m)
                fulfilment.Flags.Add("piggy: paid purchase found an empty bank — nothing to pay out (comp manually)");
            if (!string.IsNullOrEmpty(result.Note))
                fulfilment.Flags.Add("piggy: " + result.Note);
            _logger.LogInformation("Store piggy break {Option} paid {Amount} chips for {User} (purchase {Id}).", option, result.Amount, ctx.UserId, ctx.Purchase.Id);
        }
    }

    /// <summary>VIP Booster (Time / LevelUp) — Progression Spec §3.6, through the rail-agnostic <see cref="IVipService.ApplyVipBoosterAsync"/>.</summary>
    public sealed class VipBoosterGrantHandler : IStoreGrantHandler
    {
        private readonly IVipService _vip;

        public VipBoosterGrantHandler(IVipService vip) { _vip = vip; }

        public string Effect => StoreCatalog.EffectVipBooster;

        public async Task GrantAsync(StoreGrantContext ctx, StoreFulfilment fulfilment, CancellationToken ct)
        {
            if (!StoreCatalog.TryParseVipBooster(ctx.Product?.Effect?.Arg, out var kind))
                throw new InvalidOperationException($"Product {ctx.Product?.Id}: VipBooster effect has no valid kind.");
            var applied = await _vip.ApplyVipBoosterAsync(ctx.UserId, kind, ctx.IdemRoot);
            fulfilment.VipBoosterApplied = applied;
            if (!applied) fulfilment.Flags.Add($"vip booster {kind}: not applied (VIP disabled or not applicable)");
        }
    }

    /// <summary>
    /// Golden pass subscription — the verified store window becomes a <c>PlayerPassEntitlements</c> row via
    /// <see cref="IPassService.GrantGoldenAsync"/> (idempotent on purchaseRef). The window comes from the STORE's dates; a
    /// vendor that reported no expiry (the fake store) gets a 30-day window. Renewals append new windows sharing the
    /// original transaction id (the reconciler / webhooks drive those).
    /// </summary>
    public sealed class GoldenPassGrantHandler : IStoreGrantHandler
    {
        private readonly IPassService _pass;

        public GoldenPassGrantHandler(IPassService pass) { _pass = pass; }

        public string Effect => StoreCatalog.EffectGoldenPass;

        public async Task GrantAsync(StoreGrantContext ctx, StoreFulfilment fulfilment, CancellationToken ct)
        {
            var passKey = string.IsNullOrWhiteSpace(ctx.Product?.Effect?.Arg) ? PassCatalog.MonthlyKey : ctx.Product.Effect.Arg.Trim();
            var v = ctx.Verification;
            var now = DateTime.UtcNow;
            var startsAt = v?.SubscriptionStartUtc ?? v?.PurchaseTimeUtc ?? now;
            var expiresAt = v?.SubscriptionExpiresUtc ?? startsAt.AddDays(30);
            if (expiresAt <= startsAt) expiresAt = startsAt.AddDays(30);

            // PurchaseRef (≤96) = the store order id (Apple transactionId / Google orderId), else the transaction key clamped.
            var purchaseRef = StoreMath.FitOrHash(!string.IsNullOrWhiteSpace(ctx.Purchase.StoreOrderId) ? ctx.Purchase.StoreOrderId : ctx.Purchase.StoreTransactionId, 96);
            var originalId = StoreMath.FitOrHash(ctx.Purchase.OriginalTransactionId ?? ctx.Purchase.StoreTransactionId, 96);

            var result = await _pass.GrantGoldenAsync(ctx.UserId, passKey, "iap", purchaseRef, startsAt, expiresAt, originalId, v?.AutoRenew ?? false);
            fulfilment.Pass = result;
            if (result == null || !result.Ok)
                throw new InvalidOperationException("Golden pass could not be granted: " + (result?.Error ?? "no result"));
        }
    }
}
