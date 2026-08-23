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

    /// <summary>The two "VIP Booster" IAP SKUs (docs/VIP_SPEC.md §4): Time = extend the HOLD on the level the player
    /// already stands at; LevelUp = hold ONE level above their window band, temporarily.</summary>
    public enum VipBoosterKind { Time = 0, LevelUp = 1 }

    public interface IVipService
    {
        /// <summary>Accrue Status Points (FLAT ×1, daily-capped, never from winnings) from a settled round's EARNED
        /// wager. Idempotent per (round, user). Relights the badge. Play moves the TIER only — never the VIP level,
        /// which is bought (docs/VIP_SPEC.md §4).</summary>
        Task<long> AccrueForRoundAsync(Guid userId, decimal cleanWager, string roundId);

        /// <summary>
        /// Rail-agnostic hook for VIP-P that a verified purchase just credited (docs/VIP_SPEC.md §4): re-reads the
        /// trailing window from the VIP-P ledger and snapshots the level this purchase reached — the credit itself is
        /// the store's (the wallet already holds it). Returns the player's VIP level after, or 0 when nothing was applied
        /// (VIP off, no points, or an idempotent replay). Safe to re-drive.
        /// </summary>
        Task<int> RecordVipPointsAsync(Guid userId, decimal vipPointsCredited, string idemKey);

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

        /// <summary>Periodic review: tier PROMOTION (demotion belongs to the season roll) and a refresh of the VIP level
        /// against a window that may have drained since the player last looked. Idempotent/safe to re-run.</summary>
        Task ReviewTierAsync(Guid userId);

        /// <summary>Spend Loyalty Points to HOLD the caller's current VIP level for another period — the live, non-IAP
        /// keep-up (the LP equivalent of the cheap "Time" booster). No charge while the hold is still live.</summary>
        Task<VipMaintainResultDto> MaintainWithLpAsync(Guid userId);

        /// <summary>Apply a "VIP Booster" IAP item (rail-agnostic, idempotent on <paramref name="idemKey"/>): Time
        /// extends the hold; LevelUp holds one level above the window band. Called by the IAP receipt flow.</summary>
        Task<bool> ApplyVipBoosterAsync(Guid userId, VipBoosterKind kind, string idemKey);
    }

    /// <summary>
    /// VIP system (docs/VIP_SPEC.md §2 + §4). Two ladders that never cross:
    ///
    /// • the TIER is seasonal status — SP accrues FLAT ×1 from clean wager (never winnings) onto the Sp wallet, and the
    ///   band of this season's balance IS the tier (Bronze = the free floor at the player-level gate);
    /// • the VIP LEVEL is the money track — the band of the VIP-P bought inside the trailing window, plus a HOLD the
    ///   reaching purchase snapshots. Play cannot move it, and stopping buying drains the window: that IS the decay.
    ///
    /// The COMP multiplier (1 + tier bonus + VIP-level bonus) boosts the Loyalty/store/faucet tracks — never odds. Runs
    /// off the settle roll-up, separate from the money path — a failure here never affects balances.
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
                SpFromWagerDailyCap = config.GetValue("Vip:SpFromWagerDailyCap", 100_000L),
                DemoteHysteresis    = config.GetValue("Vip:DemoteHysteresis", 0.85m),
                WindowDays          = config.GetValue("Vip:WindowDays", 90),
                VipMaintainDays     = config.GetValue("Vip:VipMaintainDays", 30),
                VipBoosterTimeDays  = config.GetValue("Vip:VipBoosterTimeDays", 30),
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
                }
                // Play relights the badge (cosmetic) and nothing else on this track. It deliberately does NOT touch the
                // VIP level or its hold: the level is what the player BOUGHT (docs/VIP_SPEC.md §4), and a round that
                // extended the hold would let a whale keep a paid level forever by dealing one hand a month.
                profile.BadgeLitUntil = now.AddDays(cfg.BadgeWindowDays);

                // It DOES re-read the level, though — LP accrual reads the stored column to size its comp, and a window
                // that drained since the player last opened the VIP screen would otherwise keep paying the old rate for
                // as long as it took the periodic review to notice. The read is cached, so this costs a query a day.
                var (_, vipLevel) = await ResolveLevelAsync(profile, cfg, now, tracked: true);
                if (vipLevel != profile.VipLevel) profile.VipLevel = vipLevel;

                profile.UpdatedAt = now;

                try { await _db.SaveChangesAsync(); }
                catch (DbUpdateConcurrencyException) when (attempt < 4) { _db.ChangeTracker.Clear(); continue; }

                // The SP credit lands AFTER the profile is safely saved, and outside the retry loop. Inside it, the
                // wallet's own SaveChanges would flush and commit the tracked profile mid-attempt, so a concurrency retry
                // would re-apply the daily cap, lifetime SP and the level grind on top of a commit that already happened.
                // SP is a wallet currency whose balance IS this season's total (docs/VIP_SPEC.md §2) — nothing debits it
                // until the roll, so the band of that balance is always the tier the player has climbed to.
                await _wallet.CreditAsync(userId.ToString(), CurrencyType.Sp, granted, TransactionType.Bonus,
                    Loyalty.LoyaltyService.Key("spw", roundId, userId), new WalletContext { Description = "Status: play" });
                return granted;
            }
        }

        public async Task<int> RecordVipPointsAsync(Guid userId, decimal vipPointsCredited, string idemKey)
        {
            if (!_cfg.Enabled || vipPointsCredited <= 0m || string.IsNullOrEmpty(idemKey)) return 0;
            // The KEY guards the hold, not the money: the VIP-P itself was credited by the store under its own line key,
            // and re-reading the window is harmless. What must not repeat is the hold this purchase bought — replaying a
            // receipt a month later would silently re-arm a level the player stopped paying for.
            if (!await _redis.GetDatabase().StringSetAsync($"vippts:{idemKey}", "1", TimeSpan.FromDays(60), When.NotExists))
                return 0;

            var cfg = await EffectiveCfgAsync();

            for (int attempt = 1; ; attempt++)
            {
                var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
                if (profile == null) return 0;
                var now = DateTime.UtcNow;

                // Forced: a credit just landed, so the cached window is known-stale by exactly this purchase.
                var (points, validUntil) = await ComputeWindowAsync(userId, cfg, now);
                profile.VipWindowPoints = points;
                profile.VipWindowValidUntil = validUntil;

                var band = VipMath.LevelFromPoints(points, cfg);
                // "Reaches or KEEPS the band" (docs/VIP_SPEC.md §4). A purchase that lands the player at or above the level
                // they are already holding re-arms the hold at the new band; one BELOW it does not — which is the whole
                // reason the rule exists: a $1.99 pack with 10 VIP-P must not hold VIP 10 forever. Once the old hold has
                // expired there is nothing left to protect, so the snapshot simply becomes whatever the window now says.
                if (VipMath.ShouldRearmHold(band, profile.VipHeldLevel, profile.VipLevelMaintainedThrough, now))
                {
                    profile.VipHeldLevel = band;
                    profile.VipLevelMaintainedThrough = band > 0 ? now.AddDays(VipMath.HoldDays(band, cfg)) : (DateTime?)null;
                }

                profile.VipLevel = VipMath.EffectiveLevel(points, profile.VipHeldLevel, profile.VipLevelMaintainedThrough, now, cfg);
                profile.BadgeLitUntil = now.AddDays(cfg.BadgeWindowDays);
                profile.UpdatedAt = now;

                try { await _db.SaveChangesAsync(); }
                catch (DbUpdateConcurrencyException) when (attempt < 4) { _db.ChangeTracker.Clear(); continue; }

                _logger.LogInformation("VIP points {Points} credited for {UserId}: window {Window}, band {Band}, level {Level} through {Through}",
                    vipPointsCredited, userId, points, band, profile.VipLevel, profile.VipLevelMaintainedThrough);
                return profile.VipLevel;
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

            var (windowPoints, vipLevel) = await ResolveLevelAsync(profile, cfg, now, tracked: false);

            // "At the top" is the LADDER's top, not a literal 10 — the level ladder is admin-editable. Asking
            // PointsRequired past the end returns long.MaxValue, which would surface as an absurd "points to next".
            long vipPtsToNext = vipLevel >= VipMath.TopVipLevel(cfg)
                ? 0L
                : Math.Max(0L, VipMath.PointsRequired(vipLevel + 1, cfg) - windowPoints);

            return new VipStatusDto
            {
                Tier = (int)tier,
                TierName = TierName(tier),
                HasBadge = VipMath.HasBadge(tier),
                BadgeLit = lit,
                HideBadge = profile.HideVipBadge,
                StatusPoints = sp,
                LifetimeStatusPoints = profile.LifetimeStatusPoints,
                BenefitMultiplier = VipMath.ComboMultiplier(tier, vipLevel, cfg),   // 1 + tier bonus + VIP-level bonus
                NextTier = nextTier,
                NextTierName = nextName,
                SpToNextTier = spToNext,
                SpendToNextTierUsd = spendToNext,
                VipLevel = vipLevel,
                VipPointsWindow = windowPoints,
                VipPointsToNextLevel = vipPtsToNext,
                VipWindowDays = cfg.WindowDays,
                VipHeldLevel = profile.VipHeldLevel,
                VipLevelMaintainedThrough = profile.VipLevelMaintainedThrough,
            };
        }

        public async Task<VipPerks> GetPerksAsync(Guid userId)
        {
            var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return new VipPerks(VipTier.None, 0, 1.0m);
            var cfg = await EffectiveCfgAsync();
            var now = DateTime.UtcNow;
            var (sp, spend) = await TrailingAsync(userId, cfg, now);
            var tier = (VipTier)Math.Max((int)profile.VipTier, (int)VipMath.ResolveBand(sp, spend, profile.Level, cfg));
            var (_, vipLevel) = await ResolveLevelAsync(profile, cfg, now, tracked: false);
            return new VipPerks(tier, vipLevel, VipMath.ComboMultiplier(tier, vipLevel, cfg));
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
                var now = DateTime.UtcNow;
                // The level being held is the one the player STANDS at right now — the window band, or a hold above it.
                // Reading it from the stored column would sell a hold on a level the window drained away weeks ago.
                var (_, level) = await ResolveLevelAsync(profile, cfg, now, tracked: false);
                if (level <= 0) return MaintFail("No VIP level to maintain.");
                if (profile.VipLevelMaintainedThrough.HasValue && now < profile.VipLevelMaintainedThrough.Value)
                    return new VipMaintainResultDto { Ok = true, AlreadyMaintained = true, LoyaltyPoints = await LpBalanceAsync(userId),
                        VipLevel = level, MaintainedThrough = profile.VipLevelMaintainedThrough };

                // LP is a wallet currency (docs/VIP_SPEC.md §3): the debit is never-negative under the wallet's own row lock
                // and idempotent on the correlation id, which is keyed to the PERIOD being bought — so a double-tap inside
                // one period charges once, and next month's maintain is a different id.
                var cost = VipMath.MaintainLpCost(level, cfg);
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
                            new WalletContext { Description = $"Maintain VIP {level}" });
                        await _db.UserProfiles.Where(p => p.UserId == userId).ExecuteUpdateAsync(s => s
                            .SetProperty(p => p.VipLevelMaintainedThrough, through)
                            .SetProperty(p => p.VipHeldLevel, level)
                            .SetProperty(p => p.VipLevel, level));
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
                    await _db.UserProfiles.Where(p => p.UserId == userId).ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.VipLevelMaintainedThrough, through)
                        .SetProperty(p => p.VipHeldLevel, level)
                        .SetProperty(p => p.VipLevel, level));
                }

                return new VipMaintainResultDto { Ok = true, LoyaltyPoints = await LpBalanceAsync(userId), VipLevel = level, MaintainedThrough = through };
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
                var (points, _) = await ResolveLevelAsync(profile, cfg, now, tracked: true);
                var band = VipMath.LevelFromPoints(points, cfg);

                if (kind == VipBoosterKind.LevelUp)
                {
                    // ONE level over the WINDOW BAND, temporarily (docs/VIP_SPEC.md §4) — over the band, not over the
                    // held level, so buying this every month cannot ratchet a player up a ladder they never paid for.
                    // Capped by the LADDER, so a shortened ladder can't be climbed past its top by buying boosters.
                    var target = Math.Min(VipMath.TopVipLevel(cfg), band + 1);
                    if (target <= 0) return false;
                    profile.VipHeldLevel = Math.Max(profile.VipHeldLevel, target);
                    profile.VipLevelMaintainedThrough = now.AddDays(VipMath.HoldDays(target, cfg));
                }
                else   // Time — extend the hold (from the later of now / current expiry), level unchanged
                {
                    // Nothing to hold means nothing to sell: a player with no level gets the "not applied" flag on the
                    // fulfilment rather than a silently burnt purchase, so an admin can comp it.
                    var holdLevel = profile.VipHeldLevel > 0 ? profile.VipHeldLevel : band;
                    if (holdLevel <= 0) return false;
                    var baseThrough = (profile.VipLevelMaintainedThrough.HasValue && profile.VipLevelMaintainedThrough.Value > now)
                        ? profile.VipLevelMaintainedThrough.Value : now;
                    // Stacking is capped at 2 × the level's hold ahead of now (§4): time boosters are cheap, and without
                    // a ceiling a stack of them would buy a permanent level for a fraction of what reaching it costs.
                    var ceiling = now.AddDays(2.0 * VipMath.HoldDays(holdLevel, cfg));
                    var extended = baseThrough.AddDays(cfg.VipBoosterTimeDays);
                    profile.VipHeldLevel = holdLevel;
                    profile.VipLevelMaintainedThrough = extended > ceiling ? ceiling : extended;
                }

                profile.VipLevel = VipMath.EffectiveLevel(points, profile.VipHeldLevel, profile.VipLevelMaintainedThrough, now, cfg);
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

            // --- Tier: PROMOTION only. ---
            //
            // Demotion belongs to the season roll now (docs/VIP_SPEC.md §2), and it cannot happen here even in principle:
            // SP only rises within a season, so the band never falls. The old monthly one-tier-max decay + hysteresis was
            // the trailing-window model's answer to a sum that could shrink under a player who stopped playing; a season
            // answers it with a scheduled, announced reset instead of a quiet monthly slide.
            var band = VipMath.ResolveBand(sp, spend, profile.Level, cfg);
            var target = (int)band >= (int)profile.VipTier ? band : profile.VipTier;
            if (profile.Level >= cfg.VipEntryLevel && (int)target < (int)VipTier.Bronze) target = VipTier.Bronze;
            if (target != profile.VipTier) profile.VipTier = target;

            // --- VIP Level: re-read, don't decay. ---
            //
            // There is no monthly −1 step any more (docs/VIP_SPEC.md §4): the level IS the window band once the hold
            // expires, and the window drains on its own. This job exists so the STORED column keeps up for a player who
            // never opens the VIP screen — leaderboards and the admin read it.
            var (_, vipLevel) = await ResolveLevelAsync(profile, cfg, now, tracked: true);
            if (vipLevel != profile.VipLevel) profile.VipLevel = vipLevel;

            if (_db.ChangeTracker.HasChanges())
            {
                profile.UpdatedAt = now;
                try { await _db.SaveChangesAsync(); }
                catch (DbUpdateConcurrencyException) { /* a concurrent write won; the next review reconciles */ }
            }
        }

        // ---- helpers ----

        /// <summary>
        /// The VIP-P credited inside the trailing window, and the moment that answer stops being true.
        ///
        /// The window is a rolling sum, so it changes for exactly two reasons: a new credit lands (the caller forces a
        /// recompute then), or the OLDEST credit inside it ages out. The second moment is knowable — it is that credit's
        /// timestamp plus the window — so the cache carries it and nothing has to poll. An empty window is valid for one
        /// whole window: nothing can leave a sum of nothing, and a credit refreshes it anyway.
        /// </summary>
        private async Task<(long points, DateTime validUntil)> ComputeWindowAsync(Guid userId, VipConfig cfg, DateTime now)
        {
            int days = Math.Max(1, cfg.WindowDays);
            var from = now.AddDays(-days);
            var walletId = await _db.PlayerWallets.AsNoTracking()
                .Where(w => w.UserId == userId && w.Currency == CurrencyType.VipPoints)
                .Select(w => (Guid?)w.WalletId).FirstOrDefaultAsync();
            if (walletId == null) return (0L, Capped(now.AddDays(days), now));

            // EVERY movement, signed — not just the credits. A refunded purchase is rolled back as a negative VIP-P row
            // (StorePurchaseService's Rollback policy), and a window that counted only credits would leave the player
            // holding the level they charged back. An admin clawback reads the same way, which is also right. Floored at
            // zero: nothing below "no purchases" exists.
            var rows = await _db.WalletTransactions.AsNoTracking()
                .Where(t => t.WalletId == walletId.Value && t.CreatedAt >= from)
                .Select(t => new { t.Amount, t.CreatedAt })
                .ToListAsync();
            if (rows.Count == 0) return (0L, Capped(now.AddDays(days), now));

            decimal sum = 0m;
            var oldest = DateTime.MaxValue;
            foreach (var r in rows)
            {
                sum += r.Amount;
                if (r.CreatedAt < oldest) oldest = r.CreatedAt;
            }
            var points = sum <= 0m ? 0L : (long)Math.Floor(sum);
            return (points, Capped(oldest.AddDays(days), now));
        }

        /// <summary>
        /// The level the player stands at, refreshing the cached window when it has expired. <paramref name="tracked"/>
        /// says whether <paramref name="profile"/> is being tracked: a tracked entity is mutated (its own SaveChanges
        /// persists the cache), an untracked one is written through <c>ExecuteUpdate</c> — mixing the two would let a
        /// later SaveChanges overwrite the row this method just updated behind EF's back.
        /// </summary>
        private async Task<(long windowPoints, int level)> ResolveLevelAsync(UserProfile profile, VipConfig cfg, DateTime now, bool tracked)
        {
            long points = profile.VipWindowPoints;
            if (WindowCacheStale(profile, cfg, now))
            {
                try
                {
                    var (fresh, validUntil) = await ComputeWindowAsync(profile.UserId, cfg, now);
                    points = fresh;
                    if (tracked)
                    {
                        profile.VipWindowPoints = fresh;
                        profile.VipWindowValidUntil = validUntil;
                    }
                    else
                    {
                        await _db.UserProfiles.Where(p => p.UserId == profile.UserId).ExecuteUpdateAsync(s => s
                            .SetProperty(p => p.VipWindowPoints, fresh)
                            .SetProperty(p => p.VipWindowValidUntil, validUntil));
                    }
                }
                catch (Exception ex)
                {
                    // A cache that could not be refreshed is a display/perk detail, never a reason to fail the caller —
                    // the settle roll-up asks for perks on every round.
                    _logger.LogWarning(ex, "VIP window refresh failed for {UserId}", profile.UserId);
                }
            }

            var level = VipMath.EffectiveLevel(points, profile.VipHeldLevel, profile.VipLevelMaintainedThrough, now, cfg);
            if (!tracked && level != profile.VipLevel)
            {
                try { await _db.UserProfiles.Where(p => p.UserId == profile.UserId).ExecuteUpdateAsync(s => s.SetProperty(p => p.VipLevel, level)); }
                catch (Exception ex) { _logger.LogWarning(ex, "VIP level persist failed for {UserId}", profile.UserId); }
            }
            return (points, level);
        }

        /// <summary>
        /// Whether the cached window must be recomputed: never computed, past its expiry, or — the case worth spelling
        /// out — valid FURTHER OUT than one whole window, which can only mean an admin shortened <c>Vip:WindowDays</c>
        /// since it was written. Without that third test a shortened window would take up to the old window to bite.
        /// </summary>
        private static bool WindowCacheStale(UserProfile p, VipConfig cfg, DateTime now)
        {
            if (p.VipWindowValidUntil == null) return true;
            if (now >= p.VipWindowValidUntil.Value) return true;
            return p.VipWindowValidUntil.Value > now.AddDays(Math.Max(1, cfg.WindowDays));
        }

        /// <summary>
        /// How long the window cache is ever trusted, whatever the ledger says the exact expiry is. The exact expiry is
        /// only correct for VIP-P that arrived through <see cref="RecordVipPointsAsync"/>; a movement that did not — an
        /// admin adjusting a balance by hand, a refund clawback — would otherwise stay invisible for up to a whole
        /// window. A day is short enough that nothing hides for long and long enough to cost one query per player.
        /// </summary>
        private const double WindowCacheMaxHours = 24.0;

        private static DateTime Capped(DateTime validUntil, DateTime now)
        {
            var ceiling = now.AddHours(WindowCacheMaxHours);
            return validUntil > ceiling ? ceiling : validUntil;
        }

        /// <summary>
        /// This SEASON's SP, and — only when some tier still has a spend floor — the trailing USD spend.
        ///
        /// SP is the wallet balance: nothing debits it until the season roll, so the balance IS the season's total and the
        /// band of it is the tier the player has climbed to. The monthly <c>StatusPointsLedger</c> buckets are no longer
        /// written — the wallet ledger is the audit now. Spend comes from <c>StorePurchases</c>, the actual record of money;
        /// the floors ship at 0 because money buys VIP-P rather than status (docs/VIP_SPEC.md §2), so the query is skipped
        /// entirely unless an admin re-imposes one.
        /// </summary>
        private async Task<(long sp, decimal spend)> TrailingAsync(Guid userId, VipConfig cfg, DateTime now)
        {
            var sp = (long)Math.Floor(await _wallet.GetBalanceAsync(userId.ToString(), CurrencyType.Sp));

            bool anyFloor = false;
            if (cfg.SpendFloorsUsd != null)
                foreach (var f in cfg.SpendFloorsUsd) if (f > 0m) { anyFloor = true; break; }
            if (!anyFloor) return (sp, decimal.MaxValue);   // nothing to clear — never pay for the query

            var windowStart = now.AddMonths(-Math.Max(1, cfg.TierWindowMonths));
            var spend = await _db.StorePurchases.AsNoTracking()
                .Where(s => s.UserId == userId && !s.IsTest && s.CreatedAt >= windowStart
                         && (s.Status == StorePurchaseStatus.Granted || s.Status == StorePurchaseStatus.Refunded))
                .SumAsync(s => (decimal?)s.UsdReference) ?? 0m;
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
