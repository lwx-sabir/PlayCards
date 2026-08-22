using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Progression;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Vip;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Khela.Game.Services.Loyalty
{
    public interface ILoyaltyService
    {
        /// <summary>Accrue Loyalty Points from a settled round's EARNED (clean) wager × the player's VIP benefit
        /// multiplier. Idempotent per (round, user). Returns the LP granted.</summary>
        Task<long> AccrueForRoundAsync(Guid userId, decimal cleanWager, string roundId);

        /// <summary>The caller's LP balance + the store catalog (affordability + VIP gate annotated).</summary>
        Task<LoyaltyStoreDto> GetStoreAsync(Guid userId);

        /// <summary>Redeem a store item. Idempotent + money-safe on <paramref name="idemKey"/>: a Redis drive-lock
        /// serializes concurrent attempts, a ledger row + step-flags make a crash mid-redeem recover on retry, the LP
        /// debit is an atomic balance-guarded UPDATE, and the chip credit rides the wallet's CorrelationId idempotency.</summary>
        Task<RedeemResultDto> RedeemAsync(Guid userId, string itemId, string idemKey);

        /// <summary>Rail-agnostic purchase hook (Progression Spec §4): the LP drip on a VERIFIED real-money purchase
        /// (<c>Loyalty:LpPerUsd</c> × VIP multiplier). Idempotent on <paramref name="idemKey"/>. Inert while <c>LpPerUsd</c> = 0.
        /// Returns the LP granted.</summary>
        Task<long> RecordPurchaseAsync(Guid userId, decimal usdSpent, string idemKey);
    }

    /// <summary>
    /// Loyalty Points (Progression Spec §4) — the redeemable comp currency. LP accrues as a small fraction of clean
    /// wager × the VIP benefit multiplier (never from winnings), into <see cref="UserProfile.LoyaltyPoints"/>; it is
    /// SPENT in a server-defined store, all redemptions flowing through the idempotent <see cref="IWalletService"/> so
    /// the store can't double-spend. Redeemed chips land in the EARNED bucket (a reward of play, kept clean). Runs off
    /// the settle roll-up; a failure never affects balances.
    /// </summary>
    public sealed class LoyaltyService : ILoyaltyService
    {
        private const string SettingsHashKey = "khela:settings";

        private readonly AppDbContext _db;
        private readonly IWalletService _wallet;
        private readonly IVipService _vip;
        private readonly IRedisService _redis;
        private readonly ILogger<LoyaltyService> _logger;
        private readonly LoyaltyConfig _cfg;

        public LoyaltyService(AppDbContext db, IWalletService wallet, IVipService vip, IRedisService redis,
            IConfiguration config, ILogger<LoyaltyService> logger)
        {
            _db = db; _wallet = wallet; _vip = vip; _redis = redis; _logger = logger;
            _cfg = new LoyaltyConfig
            {
                Enabled         = config.GetValue("Loyalty:Enabled", true),
                LpChipsPerPoint = config.GetValue("Loyalty:LpChipsPerPoint", 100m),
                LpPerUsd        = config.GetValue("Loyalty:LpPerUsd", 0m),
                // Catalog keeps the LoyaltyConfig code defaults.
            };
        }

        public async Task<long> AccrueForRoundAsync(Guid userId, decimal cleanWager, string roundId)
        {
            if (!_cfg.Enabled || string.IsNullOrEmpty(roundId)) return 0;
            if (!await _redis.GetDatabase().StringSetAsync($"loyacc:{roundId}:{userId}", "1", TimeSpan.FromDays(30), When.NotExists))
                return 0;

            var cfg = await EffectiveCfgAsync();
            for (int attempt = 1; ; attempt++)
            {
                var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
                if (profile == null) return 0;
                var mult = _vip.ComboMultiplier(profile.VipTier, profile.VipLevel);   // VIP tier + level boost LP earning (the benefit track)
                var lp = LoyaltyMath.LpFromWager(cleanWager, mult, cfg);
                if (lp <= 0) return 0;

                profile.LoyaltyPoints += lp;
                profile.LifetimeLoyaltyPoints += lp;
                profile.UpdatedAt = DateTime.UtcNow;
                try { await _db.SaveChangesAsync(); return lp; }
                catch (DbUpdateConcurrencyException) when (attempt < 4) { _db.ChangeTracker.Clear(); continue; }
            }
        }

        public async Task<long> RecordPurchaseAsync(Guid userId, decimal usdSpent, string idemKey)
        {
            if (!_cfg.Enabled || usdSpent <= 0m || string.IsNullOrEmpty(idemKey)) return 0;
            var cfg = await EffectiveCfgAsync();
            if (cfg.LpPerUsd <= 0m) return 0;   // dormant until the drip is switched on — checked before the idempotency mark so turning it on later still pays
            if (!await _redis.GetDatabase().StringSetAsync($"loypur:{idemKey}", "1", TimeSpan.FromDays(60), When.NotExists))
                return 0;

            for (int attempt = 1; ; attempt++)
            {
                var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
                if (profile == null) return 0;
                var mult = _vip.ComboMultiplier(profile.VipTier, profile.VipLevel);
                var lp = LoyaltyMath.LpFromPurchase(usdSpent, mult, cfg);
                if (lp <= 0) return 0;

                profile.LoyaltyPoints += lp;
                profile.LifetimeLoyaltyPoints += lp;
                profile.UpdatedAt = DateTime.UtcNow;
                try { await _db.SaveChangesAsync(); return lp; }
                catch (DbUpdateConcurrencyException) when (attempt < 4) { _db.ChangeTracker.Clear(); continue; }
            }
        }

        public async Task<LoyaltyStoreDto> GetStoreAsync(Guid userId)
        {
            var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return null;
            var cfg = await EffectiveCfgAsync();

            var items = new List<LoyaltyStoreItemDto>();
            foreach (var i in cfg.Catalog)
                items.Add(new LoyaltyStoreItemDto
                {
                    Id = i.Id, Name = i.Name, Kind = i.Kind, CostLp = i.CostLp, ChipAmount = i.ChipAmount,
                    MinVipTier = i.MinVipTier,
                    Affordable = profile.LoyaltyPoints >= i.CostLp,
                    Unlocked = (int)profile.VipTier >= i.MinVipTier,
                });

            return new LoyaltyStoreDto
            {
                Points = profile.LoyaltyPoints,
                LifetimePoints = profile.LifetimeLoyaltyPoints,
                Items = items,
            };
        }

        public async Task<RedeemResultDto> RedeemAsync(Guid userId, string itemId, string idemKey)
        {
            if (!_cfg.Enabled) return Fail(itemId, "Loyalty store is disabled.");
            if (string.IsNullOrWhiteSpace(idemKey)) return Fail(itemId, "Missing idempotency key.");

            var cfg = await EffectiveCfgAsync();
            LoyaltyStoreItem item = null;
            foreach (var i in cfg.Catalog) if (i.Id == itemId) { item = i; break; }
            if (item == null) return Fail(itemId, "Unknown item.");
            if (item.Kind != "chips") return Fail(itemId, "Unsupported item kind.");

            var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return Fail(itemId, "No profile.");
            if ((int)profile.VipTier < item.MinVipTier) return Fail(itemId, "VIP tier too low for this item.");

            // Serialize concurrent attempts on the same idemKey (cross-request/instance); the ledger row + flags below
            // make a crash mid-redeem recover on a later retry (after this short lock auto-expires).
            var lockKey = $"loyredeem:lock:{idemKey}";
            if (!await _redis.GetDatabase().StringSetAsync(lockKey, "1", TimeSpan.FromSeconds(30), When.NotExists))
                return Fail(itemId, "Redeem already in progress — retry shortly.");

            try
            {
                var red = await _db.LoyaltyRedemptions.FirstOrDefaultAsync(r => r.IdempotencyKey == idemKey);
                if (red != null && red.UserId != userId) return Fail(itemId, "Idempotency key conflict.");
                if (red != null && red.Status == "Completed")
                    return await OkAsync(userId, red.ItemId, red.ChipAmount);   // idempotent replay
                if (red != null && red.Status == "Failed") return Fail(itemId, "This redemption previously failed.");

                if (red == null)
                {
                    red = new LoyaltyRedemption
                    {
                        UserId = userId, IdempotencyKey = idemKey, ItemId = item.Id, Kind = item.Kind,
                        CostLp = item.CostLp, ChipAmount = item.ChipAmount,
                    };
                    _db.LoyaltyRedemptions.Add(red);
                    await _db.SaveChangesAsync();
                }

                // STEP 1 — debit LP atomically (balance-guarded; the flag stops a retry from double-debiting).
                if (!red.LpDeducted)
                {
                    var rows = await _db.UserProfiles
                        .Where(p => p.UserId == userId && p.LoyaltyPoints >= red.CostLp)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.LoyaltyPoints, p => p.LoyaltyPoints - red.CostLp));
                    if (rows == 0)
                    {
                        red.Status = "Failed";
                        await _db.SaveChangesAsync();
                        return Fail(itemId, "Insufficient Loyalty Points.");
                    }
                    red.LpDeducted = true;
                    await _db.SaveChangesAsync();
                }

                // STEP 2 — credit the chips (idempotent on the wallet CorrelationId; lands in the EARNED bucket).
                if (!red.ChipsCredited)
                {
                    await _wallet.CreditAsync(userId.ToString(), CurrencyType.Chips, red.ChipAmount, TransactionType.Bonus,
                        $"loy:{idemKey}", new WalletContext { Description = $"Loyalty redeem: {item.Name}" });
                    red.ChipsCredited = true;
                    await _db.SaveChangesAsync();
                }

                red.Status = "Completed";
                red.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                return await OkAsync(userId, red.ItemId, red.ChipAmount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loyalty redeem failed for user {UserId} item {ItemId} key {Key}", userId, itemId, idemKey);
                return Fail(itemId, "Redeem failed — please retry.");   // ledger row + flags let a retry resume safely
            }
            finally
            {
                try { await _redis.GetDatabase().KeyDeleteAsync(lockKey); } catch { /* lock auto-expires anyway */ }
            }
        }

        // ---- helpers ----

        private async Task<RedeemResultDto> OkAsync(Guid userId, string itemId, decimal chipAmount)
        {
            var balance = await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId)
                .Select(p => p.LoyaltyPoints).FirstOrDefaultAsync();
            return new RedeemResultDto { Ok = true, ItemId = itemId, Points = balance, ChipAmount = chipAmount };
        }

        private static RedeemResultDto Fail(string itemId, string error)
            => new RedeemResultDto { Ok = false, Error = error, ItemId = itemId };

        private async Task<LoyaltyConfig> EffectiveCfgAsync()
        {
            try
            {
                var entries = await _redis.GetDatabase().HashGetAllAsync(SettingsHashKey);
                if (entries == null || entries.Length == 0) return _cfg;
                var map = new Dictionary<string, string>(entries.Length);
                foreach (var e in entries) map[(string)e.Name] = (string)e.Value;
                return LoyaltyConfig.Overlay(_cfg, map);
            }
            catch { return _cfg; }
        }
    }
}
