using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Leaderboards;
using Khela.Common.Progression;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Khela.Game.Services.Vip
{
    /// <summary>VIP perks other services read. <see cref="Multiplier"/> = 1 + tier bonus + VIP-level bonus, and rides
    /// the Loyalty/store/faucet tracks ONLY — never Status Points and NEVER winnings.</summary>
    public readonly record struct VipPerks(VipTier Tier, int VipLevel, decimal Multiplier);

    /// <summary>The two "VIP Booster" IAP SKUs (§3.6): Time = extend the current level's maintenance window;
    /// LevelUp = +1 VIP level (to the next).</summary>
    public enum VipBoosterKind { Time = 0, LevelUp = 1 }

    public interface IVipService
    {
        /// <summary>Accrue Status Points (FLAT ×1, daily-capped, never from winnings) AND grind VIP-Level progress from a
        /// settled round's EARNED wager. Idempotent per (round, user). Relights the badge + maintains the VIP level.</summary>
        Task<long> AccrueForRoundAsync(Guid userId, decimal cleanWager, string roundId);

        /// <summary>Rail-agnostic purchase hook (IAP now, web store later): credit SP-from-purchase + USD spend on a
        /// verified purchase. Idempotent on <paramref name="idemKey"/>. DORMANT until a purchase flow calls it.</summary>
        Task<long> RecordPurchaseAsync(Guid userId, decimal usdSpent, string idemKey);

        /// <summary>The caller's live VIP status (tier + VIP level; promotion realized here).</summary>
        Task<VipStatusDto> GetMyVipStatusAsync(Guid userId);

        /// <summary>The player's current perks (for other services, e.g. Loyalty earning).</summary>
        Task<VipPerks> GetPerksAsync(Guid userId);

        /// <summary>
        /// The COMP multiplier (1 + tier bonus + VIP-level bonus) from the EFFECTIVE config — the admin's editable tier and
        /// level ladders included. Applies to the Loyalty/store/faucet tracks ONLY, never winnings. This is the one anything
        /// that PAYS should use: <see cref="ComboMultiplier"/> reads the constructor's config, whose bonus arrays are the
        /// built-in ones.
        /// </summary>
        Task<decimal> ComboMultiplierAsync(VipTier tier, int vipLevel);

        /// <summary>The COMP multiplier from the BASE config (no admin ladder) — display/diagnostics only. Anything that
        /// grants must use <see cref="ComboMultiplierAsync"/>, or it pays at rates the admin did not set.</summary>
        decimal ComboMultiplier(VipTier tier, int vipLevel);

        /// <summary>Monthly review: gentle tier decay (one-tier-max + hysteresis; Bronze permanent floor) AND VIP-Level
        /// decay (drop 1 if unmaintained this period; floor 0). Idempotent/safe to re-run.</summary>
        Task ReviewTierAsync(Guid userId);

        /// <summary>Spend Loyalty Points to MAINTAIN (extend a period on) the caller's current VIP level — the live,
        /// non-IAP keep-up (the LP equivalent of the cheap "Time" booster). No charge if already maintained.</summary>
        Task<VipMaintainResultDto> MaintainWithLpAsync(Guid userId);

        /// <summary>Apply a "VIP Booster" IAP item (rail-agnostic, idempotent on <paramref name="idemKey"/>): Time
        /// extends the maintenance window; LevelUp grants +1 level. DORMANT — called by the IAP receipt flow.</summary>
        Task<bool> ApplyVipBoosterAsync(Guid userId, VipBoosterKind kind, string idemKey);
    }

    /// <summary>
    /// VIP system (Progression Spec §3 + §3.6). Status Points accrue FLAT ×1 from clean wager (never winnings) into a
    /// monthly <see cref="StatusPointsLedger"/>; the TIER is the band the trailing-window SP sum (+ spend floor)
    /// qualifies for (Bronze = permanent floor at the level gate). On top, a premium VIP LEVEL (1–10) is ground from
    /// SP × tier-factor (or bought) and decays 1/month if unmaintained (floor 0). The COMP multiplier
    /// (1 + tier bonus + VIP-level bonus) boosts the Loyalty/store/faucet tracks — NEVER winnings. Runs off the settle
    /// roll-up, separate from the money path — a failure here never affects balances.
    /// </summary>
    public sealed class VipService : IVipService
    {
        private const string SettingsHashKey = "khela:settings";

        private readonly AppDbContext _db;
        private readonly IRedisService _redis;
        private readonly IWalletService _wallet;
        private readonly ILogger<VipService> _logger;
        private readonly VipConfig _cfg;

        public VipService(AppDbContext db, IRedisService redis, IWalletService wallet,
            IConfiguration config, ILogger<VipService> logger)
        {
            _db = db; _redis = redis; _wallet = wallet; _logger = logger;
            _cfg = new VipConfig
            {
                Enabled             = config.GetValue("Vip:Enabled", true),
                VipEntryLevel       = config.GetValue("Vip:VipEntryLevel", 20),
                TierWindowMonths    = config.GetValue("Vip:TierWindowMonths", 12),
                BadgeWindowDays     = config.GetValue("Vip:BadgeWindowDays", 30),
                SpChipsPerPoint     = config.GetValue("Vip:SpChipsPerPoint", 50m),
                SpPerUsd            = config.GetValue("Vip:SpPerUsd", 100m),
                SpFromWagerDailyCap = config.GetValue("Vip:SpFromWagerDailyCap", 100_000L),
                DemoteHysteresis    = config.GetValue("Vip:DemoteHysteresis", 0.85m),
                VipMaintainDays     = config.GetValue("Vip:VipMaintainDays", 30),
                // per-tier / per-level arrays keep the VipConfig code defaults (the locked §3 + §3.6 ladders)
            };
        }

        public async Task<long> AccrueForRoundAsync(Guid userId, decimal cleanWager, string roundId)
        {
            if (!_cfg.Enabled || string.IsNullOrEmpty(roundId)) return 0;
            // Idempotency: the SP/level mutation is += (not idempotent), so gate durably per (round, user). SET-NX
            // first — favour no-double over no-loss (a re-run would double the SP).
            if (!await _redis.GetDatabase().StringSetAsync($"vipacc:{roundId}:{userId}", "1", TimeSpan.FromDays(30), When.NotExists))
                return 0;

            var cfg = await EffectiveCfgAsync();

            for (int attempt = 1; ; attempt++)
            {
                var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
                if (profile == null) return 0;
                var now = DateTime.UtcNow;

                // Daily wager-SP cap — lazy reset after midnight (mirrors the XP daily cap).
                if (profile.DailySpResetAt == null || now >= profile.DailySpResetAt)
                {
                    profile.DailySpFromWager = 0;
                    profile.DailySpResetAt = now.Date.AddDays(1);
                }
                var rawSp = VipMath.SpFromWager(cleanWager, cfg);
                // 0 (or less) means UNCAPPED, which is what the knob's own help promises. Read literally it would mean
                // "room = 0 - earned", i.e. every player's SP silently switched off by a knob labelled "uncapped".
                var room = cfg.SpFromWagerDailyCap <= 0L ? long.MaxValue : Math.Max(0, cfg.SpFromWagerDailyCap - profile.DailySpFromWager);
                var granted = Math.Min(rawSp, room);

                if (granted > 0)
                {
                    profile.DailySpFromWager += granted;
                    profile.LifetimeStatusPoints += granted;
                    await AddToMonthBucketAsync(userId, now, granted, 0m);

                    // VIP-Level grind (§3.6): progress = granted SP × the player's tier factor; auto level-up (cap 10).
                    var gained = VipMath.VipProgressFromSp(granted, profile.VipTier, cfg);
                    var (prog, lvl) = VipMath.ApplyVipLevelUps(profile.VipLevelProgress, profile.VipLevel, gained, cfg);
                    profile.VipLevelProgress = prog;
                    profile.VipLevel = lvl;
                }
                profile.BadgeLitUntil = now.AddDays(cfg.BadgeWindowDays);              // any settled round relights the badge (cosmetic)
                profile.VipLevelMaintainedThrough = now.AddDays(cfg.VipMaintainDays);  // ...and maintains the VIP level (§3.6)
                profile.UpdatedAt = now;

                try { await _db.SaveChangesAsync(); return granted; }
                catch (DbUpdateConcurrencyException) when (attempt < 4) { _db.ChangeTracker.Clear(); continue; }
            }
        }

        public async Task<long> RecordPurchaseAsync(Guid userId, decimal usdSpent, string idemKey)
        {
            if (!_cfg.Enabled || usdSpent <= 0m || string.IsNullOrEmpty(idemKey)) return 0;
            if (!await _redis.GetDatabase().StringSetAsync($"vippur:{idemKey}", "1", TimeSpan.FromDays(60), When.NotExists))
                return 0;

            var cfg = await EffectiveCfgAsync();
            var sp = VipMath.SpFromPurchase(usdSpent, cfg);   // purchase SP is NOT daily-capped (real spend self-limits)

            for (int attempt = 1; ; attempt++)
            {
                var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
                if (profile == null) return 0;
                var now = DateTime.UtcNow;
                profile.LifetimeStatusPoints += sp;
                profile.BadgeLitUntil = now.AddDays(cfg.BadgeWindowDays);
                profile.VipLevelMaintainedThrough = now.AddDays(cfg.VipMaintainDays);   // a purchase also maintains the VIP level
                profile.UpdatedAt = now;
                await AddToMonthBucketAsync(userId, now, sp, usdSpent);

                try { await _db.SaveChangesAsync(); return sp; }
                catch (DbUpdateConcurrencyException) when (attempt < 4) { _db.ChangeTracker.Clear(); continue; }
            }
        }

        public async Task<VipStatusDto> GetMyVipStatusAsync(Guid userId)
        {
            var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return null;
            var cfg = await EffectiveCfgAsync();
            var now = DateTime.UtcNow;

            var (sp, spend) = await TrailingAsync(userId, cfg, now);
            var band = VipMath.ResolveBand(sp, spend, profile.Level, cfg);
            var tier = (VipTier)Math.Max((int)profile.VipTier, (int)band);   // immediate promotion (lazy)

            if ((int)tier > (int)profile.VipTier)
            {
                try { await _db.UserProfiles.Where(p => p.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(p => p.VipTier, tier)); }
                catch (Exception ex) { _logger.LogWarning(ex, "VIP promotion persist failed for {UserId}", userId); }
            }

            bool lit = VipMath.HasBadge(tier) && profile.BadgeLitUntil.HasValue && now < profile.BadgeLitUntil.Value;

            int? nextTier = null; string nextName = null; long spToNext = 0; decimal spendToNext = 0m;
            if (tier == VipTier.None)
            {
                nextTier = (int)VipTier.Bronze; nextName = TierName(VipTier.Bronze);   // reached by LEVELING, not SP
            }
            else if ((int)tier < (int)VipTier.BlackDiamond)
            {
                int n = (int)tier + 1;
                nextTier = n; nextName = TierName((VipTier)n);
                spToNext = Math.Max(0, VipMath.SpBar(cfg, n) - sp);
                spendToNext = Math.Max(0m, VipMath.SpendFloor(cfg, n) - spend);
            }

            // "At the top" is the LADDER's top, not a literal 10 — the level ladder is admin-editable. Asking
            // VipLevelThreshold past the end returns long.MaxValue, which would surface as an absurd "progress to next".
            long vipProgToNext = profile.VipLevel >= VipMath.TopVipLevel(cfg)
                ? 0L
                : Math.Max(0L, VipMath.VipLevelThreshold(profile.VipLevel + 1, cfg) - profile.VipLevelProgress);

            return new VipStatusDto
            {
                Tier = (int)tier,
                TierName = TierName(tier),
                HasBadge = VipMath.HasBadge(tier),
                BadgeLit = lit,
                HideBadge = profile.HideVipBadge,
                StatusPoints = sp,
                LifetimeStatusPoints = profile.LifetimeStatusPoints,
                BenefitMultiplier = VipMath.ComboMultiplier(tier, profile.VipLevel, cfg),   // 1 + tier bonus + VIP-level bonus
                NextTier = nextTier,
                NextTierName = nextName,
                SpToNextTier = spToNext,
                SpendToNextTierUsd = spendToNext,
                VipLevel = profile.VipLevel,
                VipLevelProgress = profile.VipLevelProgress,
                VipLevelProgressToNext = vipProgToNext,
                VipLevelMaintainedThrough = profile.VipLevelMaintainedThrough,
            };
        }

        public async Task<VipPerks> GetPerksAsync(Guid userId)
        {
            var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return new VipPerks(VipTier.None, 0, 1.0m);
            var cfg = await EffectiveCfgAsync();
            var (sp, spend) = await TrailingAsync(userId, cfg, DateTime.UtcNow);
            var tier = (VipTier)Math.Max((int)profile.VipTier, (int)VipMath.ResolveBand(sp, spend, profile.Level, cfg));
            return new VipPerks(tier, profile.VipLevel, VipMath.ComboMultiplier(tier, profile.VipLevel, cfg));
        }

        /// <summary>
        /// The comp multiplier from the EFFECTIVE config — the admin's ladders included. It has to be async: the tier and
        /// level bonus arrays are admin-editable (Settings ▸ VIP &amp; Loyalty), and <c>_cfg</c> is the constructor's copy,
        /// which carries only the appsettings scalars. Reading them from <c>_cfg</c> would grant LP at the built-in rates
        /// while the VIP screen showed the admin's — the earning silently ignoring the one column the editor exists to tune.
        /// </summary>
        public async Task<decimal> ComboMultiplierAsync(VipTier tier, int vipLevel)
            => VipMath.ComboMultiplier(tier, vipLevel, await EffectiveCfgAsync());

        public decimal ComboMultiplier(VipTier tier, int vipLevel) => VipMath.ComboMultiplier(tier, vipLevel, _cfg);

        public async Task<VipMaintainResultDto> MaintainWithLpAsync(Guid userId)
        {
            if (!_cfg.Enabled) return MaintFail("VIP is disabled.");
            var cfg = await EffectiveCfgAsync();
            var lockKey = $"vipmaint:lock:{userId}";   // serialize concurrent maintains (prevents a double LP debit)
            if (!await _redis.GetDatabase().StringSetAsync(lockKey, "1", TimeSpan.FromSeconds(15), When.NotExists))
                return MaintFail("Maintain already in progress — retry shortly.");
            try
            {
                var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
                if (profile == null) return MaintFail("No profile.");
                if (profile.VipLevel <= 0) return MaintFail("No VIP level to maintain.");
                var now = DateTime.UtcNow;
                if (profile.VipLevelMaintainedThrough.HasValue && now < profile.VipLevelMaintainedThrough.Value)
                    return new VipMaintainResultDto { Ok = true, AlreadyMaintained = true, LoyaltyPoints = await LpBalanceAsync(userId),
                        VipLevel = profile.VipLevel, MaintainedThrough = profile.VipLevelMaintainedThrough };

                // LP is a wallet currency (docs/VIP_SPEC.md §3): the debit is never-negative under the wallet's own row lock
                // and idempotent on the correlation id, which is keyed to the PERIOD being bought — so a double-tap inside
                // one period charges once, and next month's maintain is a different id.
                var cost = VipMath.MaintainLpCost(profile.VipLevel, cfg);
                var through = now.AddDays(cfg.VipMaintainDays);
                if (cost > 0L)
                {
                    // The correlation id names the period being REPLACED, not `now`: keyed on `through` a retry seconds
                    // later — across a midnight boundary, or after the maintain days were retuned — is a different id and
                    // charges the player twice for one period. The window it extends from is the same on every retry.
                    var replacing = profile.VipLevelMaintainedThrough?.ToString("yyyyMMddHHmmss") ?? "new";
                    var corr = $"vipmt:{userId:N}:{replacing}";

                    // We own the transaction: the debit and the window it buys commit together, and a throw from the
                    // wallet (which leaves ITS transaction open) can't strand a charge with no window.
                    await using var tx = await _db.Database.BeginTransactionAsync();
                    try
                    {
                        await _wallet.DebitAsync(userId.ToString(), CurrencyType.Lp, cost, TransactionType.Purchase, corr,
                            new WalletContext { Description = $"Maintain VIP {profile.VipLevel}" });
                        await _db.UserProfiles.Where(p => p.UserId == userId)
                            .ExecuteUpdateAsync(s => s.SetProperty(p => p.VipLevelMaintainedThrough, through));
                        await tx.CommitAsync();
                    }
                    catch (InsufficientFundsException)
                    {
                        await tx.RollbackAsync();
                        return MaintFail("Insufficient Loyalty Points.");
                    }
                }
                else
                {
                    await _db.UserProfiles.Where(p => p.UserId == userId)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.VipLevelMaintainedThrough, through));
                }

                return new VipMaintainResultDto { Ok = true, LoyaltyPoints = await LpBalanceAsync(userId), VipLevel = profile.VipLevel, MaintainedThrough = through };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VIP LP-maintain failed for {UserId}", userId);
                return MaintFail("Maintain failed — please retry.");
            }
            finally { try { await _redis.GetDatabase().KeyDeleteAsync(lockKey); } catch { } }
        }

        public async Task<bool> ApplyVipBoosterAsync(Guid userId, VipBoosterKind kind, string idemKey)
        {
            if (!_cfg.Enabled || string.IsNullOrEmpty(idemKey)) return false;
            if (!await _redis.GetDatabase().StringSetAsync($"vipboost:{idemKey}", "1", TimeSpan.FromDays(60), When.NotExists))
                return true;   // already applied (idempotent replay)
            var cfg = await EffectiveCfgAsync();
            for (int attempt = 1; ; attempt++)
            {
                var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
                if (profile == null) return false;
                var now = DateTime.UtcNow;
                if (kind == VipBoosterKind.LevelUp)
                {
                    // Capped by the LADDER, so a shortened ladder can't be climbed past its top by buying boosters.
                    profile.VipLevel = Math.Min(VipMath.TopVipLevel(cfg), profile.VipLevel + 1);
                    profile.VipLevelMaintainedThrough = now.AddDays(cfg.VipMaintainDays);
                }
                else   // Time — extend the maintenance window (from the later of now / current expiry)
                {
                    var baseThrough = (profile.VipLevelMaintainedThrough.HasValue && profile.VipLevelMaintainedThrough.Value > now)
                        ? profile.VipLevelMaintainedThrough.Value : now;
                    profile.VipLevelMaintainedThrough = baseThrough.AddDays(cfg.VipBoosterTimeDays);
                }
                profile.UpdatedAt = now;
                try { await _db.SaveChangesAsync(); return true; }
                catch (DbUpdateConcurrencyException) when (attempt < 4) { _db.ChangeTracker.Clear(); continue; }
            }
        }

        private static VipMaintainResultDto MaintFail(string error) => new VipMaintainResultDto { Ok = false, Error = error };

        /// <summary>The player's spendable LP — the wallet balance (docs/VIP_SPEC.md §3), floored to a whole point.</summary>
        private async Task<long> LpBalanceAsync(Guid userId)
            => (long)Math.Floor(await _wallet.GetBalanceAsync(userId.ToString(), CurrencyType.Lp));

        public async Task ReviewTierAsync(Guid userId)
        {
            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return;
            var cfg = await EffectiveCfgAsync();
            var now = DateTime.UtcNow;
            var (sp, spend) = await TrailingAsync(userId, cfg, now);

            // --- Tier: gentle decay (§3.4) — one-tier-max, only below the hysteresis bar; Bronze permanent floor. ---
            var band = VipMath.ResolveBand(sp, spend, profile.Level, cfg);
            var current = profile.VipTier;
            VipTier target;
            if ((int)band >= (int)current) target = band;   // promotion / hold
            else
            {
                bool underBar = sp < (long)(VipMath.SpBar(cfg, (int)current) * cfg.DemoteHysteresis)
                              || spend < VipMath.SpendFloor(cfg, (int)current) * cfg.DemoteHysteresis;
                target = underBar ? (VipTier)((int)current - 1) : current;
            }
            if (profile.Level >= cfg.VipEntryLevel && (int)target < (int)VipTier.Bronze) target = VipTier.Bronze;
            if ((int)target < 0) target = VipTier.None;
            if (target != current) profile.VipTier = target;

            // --- VIP Level: drop 1 if unmaintained this period (no play / LP / IAP top-up); floor 0; fresh window after. ---
            if (profile.VipLevel > 0 &&
                (profile.VipLevelMaintainedThrough == null || now > profile.VipLevelMaintainedThrough.Value))
            {
                profile.VipLevel -= 1;
                profile.VipLevelMaintainedThrough = now.AddDays(cfg.VipMaintainDays);
            }

            if (_db.ChangeTracker.HasChanges())
            {
                profile.UpdatedAt = now;
                try { await _db.SaveChangesAsync(); }
                catch (DbUpdateConcurrencyException) { /* a concurrent write won; the next review reconciles */ }
            }
        }

        // ---- helpers ----

        private async Task AddToMonthBucketAsync(Guid userId, DateTime now, long sp, decimal usd)
        {
            var monthStart = new DateTime(now.Year, now.Month, 1);
            var bucket = await _db.StatusPointsLedgers.FirstOrDefaultAsync(b => b.UserId == userId && b.PeriodStart == monthStart);
            if (bucket == null)
            {
                bucket = new StatusPointsLedger { UserId = userId, PeriodStart = monthStart };
                _db.StatusPointsLedgers.Add(bucket);
            }
            bucket.Sp += sp;
            bucket.SpendUsd += usd;
            bucket.UpdatedAt = now;
        }

        private async Task<(long sp, decimal spend)> TrailingAsync(Guid userId, VipConfig cfg, DateTime now)
        {
            var windowStart = new DateTime(now.Year, now.Month, 1).AddMonths(-(Math.Max(1, cfg.TierWindowMonths) - 1));
            var rows = await _db.StatusPointsLedgers.AsNoTracking()
                .Where(b => b.UserId == userId && b.PeriodStart >= windowStart)
                .ToListAsync();
            long sp = 0; decimal spend = 0m;
            foreach (var r in rows) { sp += r.Sp; spend += r.SpendUsd; }
            return (sp, spend);
        }

        private static string TierName(VipTier t) => t switch
        {
            VipTier.RoyalDiamond => "Royal Diamond",
            VipTier.BlackDiamond => "Black Diamond",
            _ => t.ToString(),
        };

        private async Task<VipConfig> EffectiveCfgAsync()
        {
            try
            {
                var entries = await _redis.GetDatabase().HashGetAllAsync(SettingsHashKey);
                if (entries == null || entries.Length == 0) return _cfg;
                var map = new Dictionary<string, string>(entries.Length);
                foreach (var e in entries) map[(string)e.Name] = (string)e.Value;
                return VipConfig.Overlay(_cfg, map);
            }
            catch { return _cfg; }
        }
    }
}
