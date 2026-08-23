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
    /// Loyalty Points (docs/VIP_SPEC.md §3) — the redeemable comp currency. LP accrues as a small fraction of clean wager
    /// × the VIP benefit multiplier (never from winnings) and is SPENT on chips; every movement rides the idempotent
    /// <see cref="IWalletService"/> so nothing can double-spend. Redeemed chips land in the EARNED bucket (a reward of
    /// play, kept clean). Runs off the settle roll-up; a failure never affects balances.
    ///
    /// The BALANCE lives in the wallet (<see cref="CurrencyType.Lp"/>), not on the profile: LP is a currency now, so packs,
    /// rewards, chests and the Exchange move it through the same ledger as Chips — with `BalanceBefore/After`, correlation
    /// ids and `FOR UPDATE` — instead of each source needing its own code against a profile column.
    /// <see cref="UserProfile.LoyaltyPoints"/> is retired (kept at 0 by the one-shot migration); only
    /// <see cref="UserProfile.LifetimeLoyaltyPoints"/> survives, as the never-falling **LP Score**.
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
            var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return 0;
            // The EFFECTIVE multiplier (admin ladders included) — LP is granted here, so it must not read the built-in bonuses.
            var mult = await _vip.ComboMultiplierAsync(profile.VipTier, profile.VipLevel);
            var lp = LoyaltyMath.LpFromWager(cleanWager, mult, cfg);
            if (lp <= 0) return 0;

            await CreditAsync(userId, lp, Key("lpw", roundId, userId), "Loyalty: play");
            return lp;
        }

        public async Task<long> RecordPurchaseAsync(Guid userId, decimal usdSpent, string idemKey)
        {
            if (!_cfg.Enabled || usdSpent <= 0m || string.IsNullOrEmpty(idemKey)) return 0;
            var cfg = await EffectiveCfgAsync();
            if (cfg.LpPerUsd <= 0m) return 0;   // dormant until the drip is switched on — checked before the idempotency mark so turning it on later still pays
            if (!await _redis.GetDatabase().StringSetAsync($"loypur:{idemKey}", "1", TimeSpan.FromDays(60), When.NotExists))
                return 0;

            var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return 0;
            var mult = await _vip.ComboMultiplierAsync(profile.VipTier, profile.VipLevel);
            var lp = LoyaltyMath.LpFromPurchase(usdSpent, mult, cfg);
            if (lp <= 0) return 0;

            await CreditAsync(userId, lp, Key("lpp", idemKey, userId), "Loyalty: purchase");
            return lp;
        }

        public async Task<LoyaltyStoreDto> GetStoreAsync(Guid userId)
        {
            var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return null;
            var cfg = await EffectiveCfgAsync();
            var points = await BalanceAsync(userId);

            var items = new List<LoyaltyStoreItemDto>();
            foreach (var i in cfg.Catalog)
                items.Add(new LoyaltyStoreItemDto
                {
                    Id = i.Id, Name = i.Name, Kind = i.Kind, CostLp = i.CostLp, ChipAmount = i.ChipAmount,
                    MinVipTier = i.MinVipTier,
                    Affordable = points >= i.CostLp,
                    Unlocked = (int)profile.VipTier >= i.MinVipTier,
                });

            return new LoyaltyStoreDto
            {
                Points = points,
                LifetimePoints = profile.LifetimeLoyaltyPoints,   // the LP Score — never falls
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

                // STEP 1 — debit LP from the wallet (never-negative under its own row lock, idempotent on the correlation
                // id, so the flag below is belt-and-braces rather than the guard it used to be).
                if (!red.LpDeducted)
                {
                    // We own the transaction so the debit joins it: WalletService leaves ITS transaction open when it
                    // throws, and the "Failed" status written afterwards would then be rolled back with it — the row would
                    // say Pending forever while the player was told the redeem failed.
                    await using var tx = await _db.Database.BeginTransactionAsync();
                    try
                    {
                        await _wallet.DebitAsync(userId.ToString(), CurrencyType.Lp, red.CostLp, TransactionType.Purchase,
                            Key("lpr", idemKey, userId), new WalletContext { Description = $"Loyalty redeem: {item.Name}" });
                        red.LpDeducted = true;
                        await _db.SaveChangesAsync();
                        await tx.CommitAsync();
                    }
                    catch (InsufficientFundsException)
                    {
                        await tx.RollbackAsync();
                        red.Status = "Failed";
                        await _db.SaveChangesAsync();   // outside the rolled-back transaction, so it sticks
                        return Fail(itemId, "Insufficient Loyalty Points.");
                    }
                }

                // STEP 2 — credit the chips (idempotent on the wallet CorrelationId; lands in the EARNED bucket).
                if (!red.ChipsCredited)
                {
                    // Through the same helper as the debit: a long client idempotency key would otherwise overflow the
                    // ledger's 64-char CorrelationId AFTER the LP was already taken — charged, never paid. (Pre-existing;
                    // it only became reachable once the debit stopped being the thing that failed first.)
                    await _wallet.CreditAsync(userId.ToString(), CurrencyType.Chips, red.ChipAmount, TransactionType.Bonus,
                        Key("loy", idemKey, userId), new WalletContext { Description = $"Loyalty redeem: {item.Name}" });
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
            => new RedeemResultDto { Ok = true, ItemId = itemId, Points = await BalanceAsync(userId), ChipAmount = chipAmount };

        /// <summary>The player's spendable LP — the wallet balance, floored to a whole point.</summary>
        public async Task<long> BalanceAsync(Guid userId)
            => (long)Math.Floor(await _wallet.GetBalanceAsync(userId.ToString(), CurrencyType.Lp));

        /// <summary>
        /// Credit LP and keep the LP SCORE in step. The score is a cache of "LP ever credited" — the ledger is the truth —
        /// so it is bumped with an atomic UPDATE rather than a read-modify-write, which is what the old accrual loop's
        /// concurrency retries were for.
        /// </summary>
        private async Task CreditAsync(Guid userId, long lp, string correlationId, string description)
        {
            await _wallet.CreditAsync(userId.ToString(), CurrencyType.Lp, lp, TransactionType.Bonus, correlationId,
                new WalletContext { Description = description });
            await _db.UserProfiles.Where(p => p.UserId == userId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.LifetimeLoyaltyPoints, p => p.LifetimeLoyaltyPoints + lp)
                    .SetProperty(p => p.UpdatedAt, DateTime.UtcNow));
        }

        /// <summary>
        /// A wallet correlation id that fits the ledger's 64-char budget whatever the source id looks like (a round id and
        /// a user id together are already over it). Deterministic, so a retry keys the same movement.
        /// </summary>
        public static string Key(string prefix, string sourceId, Guid userId)
        {
            var plain = $"{prefix}:{sourceId}:{userId:N}";
            return plain.Length <= 64 ? plain : $"{prefix}:" + Store.StoreMath.Sha256Hex($"{sourceId}:{userId:N}").Substring(0, 48);
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
