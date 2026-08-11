using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Progression;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Rewards;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Khela.Game.Services.Progression
{
    public interface IProgressionService
    {
        /// <summary>
        /// Accrue XP for a settled round from the EARNED (clean) wager, apply level-ups + level/milestone
        /// rewards. Idempotent per (round, user). Returns the XP actually granted this round (post-cap), which
        /// the stats roll-up feeds to UserGameStats.ExperienceEarned + the XP leaderboard (single owner).
        /// </summary>
        Task<long> AccrueForRoundAsync(Guid userId, decimal cleanWager, bool win, string roundId);

        /// <summary>
        /// Grant a FLAT XP amount from any NON-wager source (daily login, quests, gifts, admin, …). Reuses the same
        /// daily-cap + auto level-up + level/milestone-reward path as round accrual, so the level rises automatically.
        /// Idempotent on <paramref name="idemKey"/> — the caller supplies a key unique within <paramref name="source"/>
        /// (e.g. source "dailylogin", idemKey "{userId}:{yyyy-MM-dd}"). Subject to the daily XP cap. Returns the XP
        /// actually granted (post-cap; 0 if duplicate, capped out, non-positive, or the layer is disabled).
        ///
        /// <paramref name="bypassDailyCap"/> pays the full amount OUTSIDE the daily budget (and without consuming it),
        /// for server-authored rewards that are gated by something other than volume — a pass claim, a purchase. It is
        /// opt-in per call: wager accrual and every other flat grant stay capped. Never expose it to a client-driven path.
        /// </summary>
        Task<long> GrantXpAsync(Guid userId, long amount, string source, string idemKey, bool bypassDailyCap = false);

        /// <summary>The caller's live level/XP state for the profile bar.</summary>
        Task<ProgressionDto> GetMyProgressionAsync(Guid userId);
    }

    /// <summary>
    /// Owns XP/Level (Progression Spec System A): wager-proportional XP on the EARNED (non-gifted) stake, a
    /// super-linear curve with carry-over, a runtime-tunable daily cap, and level-up/milestone chip rewards
    /// through the idempotent wallet. It is the SOLE writer of UserProfile.Experience/LifetimeExperience/Level
    /// — PlayerStatsService no longer touches them. Runs off the settle roll-up, separate from the money path;
    /// a failure here never affects balances (the wallet already settled).
    /// </summary>
    public sealed class ProgressionService : IProgressionService
    {
        private const string SettingsHashKey = "khela:settings";   // admin runtime overrides (dashboard SettingsController)

        private readonly AppDbContext _db;
        private readonly IRewardService _rewards;
        private readonly IRedisService _redis;
        private readonly ILogger<ProgressionService> _logger;
        private readonly ProgressionConfig _cfg;

        public ProgressionService(AppDbContext db, IRewardService rewards, IRedisService redis,
            IConfiguration config, ILogger<ProgressionService> logger)
        {
            _db = db; _rewards = rewards; _redis = redis; _logger = logger;
            _cfg = new ProgressionConfig
            {
                Enabled = config.GetValue("Progression:Enabled", true),
                XpChipsPerPoint = config.GetValue("Progression:XpChipsPerPoint", 10m),
                MaxWagerPerBet = config.GetValue("Progression:MaxWagerPerBet", 0m),
                MinBetEarly = config.GetValue("Progression:MinBetEarly", 1000m),
                MinBetLate = config.GetValue("Progression:MinBetLate", 5000m),
                EarlyMaxLevel = config.GetValue("Progression:EarlyMaxLevel", 3),
                SubFloorXpMultiplier = config.GetValue("Progression:SubFloorXpMultiplier", 0.2m),
                WinXpBonus = config.GetValue("Progression:WinXpBonus", 0.1m),
                DailyXpCap = config.GetValue("Progression:DailyXpCap", 150_000L),
                XpBase = config.GetValue("Progression:XpBase", 150L),
                XpExp = config.GetValue("Progression:XpExp", 1.6),
                LvlupBase = config.GetValue("Progression:LvlupBase", 100L),
                MilestoneEveryLevels = config.GetValue("Progression:MilestoneEveryLevels", 10),
            };
        }

        public Task<long> AccrueForRoundAsync(Guid userId, decimal cleanWager, bool win, string roundId)
        {
            if (!_cfg.Enabled || string.IsNullOrEmpty(roundId)) return Task.FromResult(0L);   // game-extension layer off → no XP
            // Wager XP is computed from the round's clean (earned, non-gifted) stake + the player's CURRENT level
            // (recomputed each attempt so an optimistic-concurrency reload uses the fresh level for the min-bet tier).
            return ApplyXpAsync(userId, $"xpacc:{roundId}:{userId}",
                (p, cfg) => ProgressionMath.RawXp(cleanWager, p.Level, win, cfg));
        }

        public Task<long> GrantXpAsync(Guid userId, long amount, string source, string idemKey, bool bypassDailyCap = false)
        {
            // Flat XP from any non-wager source (daily login, quests, gifts, admin). Same cap + auto level-up +
            // reward path as round accrual. Caller supplies an idemKey unique within the source.
            if (!_cfg.Enabled || amount <= 0 || string.IsNullOrEmpty(idemKey)) return Task.FromResult(0L);
            return ApplyXpAsync(userId, $"xpgrant:{source}:{idemKey}", (_, _) => amount, bypassDailyCap);
        }

        /// <summary>
        /// The single XP-apply core, shared by wager accrual + flat grants. Durably idempotent on <paramref name="idemKey"/>
        /// (SET-NX FIRST — favour no-double over no-loss, since the Experience/Level mutation is += and a retry would
        /// double the level-up CHIP rewards). <paramref name="rawXp"/> yields the pre-cap XP from the current profile +
        /// effective config; it is re-evaluated on each optimistic-concurrency attempt so a reload uses fresh values.
        /// Applies the daily cap (excess discarded), auto level-ups with carry-over, then idempotent per-(user,level) rewards.
        /// With <paramref name="bypassDailyCap"/> the amount is paid in full and does NOT consume the daily budget, so a
        /// reward can't quietly shrink the player's remaining round XP.
        /// </summary>
        private async Task<long> ApplyXpAsync(Guid userId, string idemKey, Func<UserProfile, ProgressionConfig, long> rawXp,
            bool bypassDailyCap = false)
        {
            if (!await _redis.GetDatabase().StringSetAsync(idemKey, "1", TimeSpan.FromDays(30), When.NotExists))
                return 0;

            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return 0;

            // Effective config = appsettings base + admin runtime overrides (Redis), read ONCE so a saved dashboard
            // change applies on the next grant. A bad/missing override falls back to appsettings.
            var cfg = await EffectiveCfgAsync();

            // OPTIMISTIC CONCURRENCY: UserProfile carries a RowVersion, so a same-user concurrent write races this row
            // and one SaveChanges throws DbUpdateConcurrencyException. Reload + re-apply from fresh values — safe
            // because level rewards are idempotent per (user,level), so a retry can never double-pay chips.
            for (int attempt = 1; ; attempt++)
            {
                var now = DateTime.UtcNow;
                // Daily-cap window — lazy reset on the first accrual after midnight (no background job needed).
                if (profile.DailyXpResetAt == null || now >= profile.DailyXpResetAt)
                {
                    profile.DailyXp = 0;
                    profile.DailyXpResetAt = now.Date.AddDays(1);   // next UTC midnight
                }

                var raw = Math.Max(0, rawXp(profile, cfg));
                long grantedXp;
                if (bypassDailyCap)
                {
                    grantedXp = raw;                 // paid in full, and NOT charged against the daily budget
                }
                else
                {
                    grantedXp = Math.Min(raw, Math.Max(0, cfg.DailyXpCap - profile.DailyXp));   // excess over the cap DISCARDED
                    profile.DailyXp += grantedXp;
                }

                List<int> crossed = null;
                if (grantedXp > 0)
                {
                    var (exp, level, cl) = ProgressionMath.ApplyLevelUps(
                        profile.Experience, profile.Level, grantedXp, cfg.XpBase, cfg.XpExp);
                    profile.Experience = exp;                  // into-level counter (carries the remainder)
                    profile.Level = level;
                    profile.LifetimeExperience += grantedXp;   // monotonic (XP-board source)
                    crossed = cl;
                }
                profile.UpdatedAt = now;

                try
                {
                    await _db.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException) when (attempt < 4)
                {
                    await _db.Entry(profile).ReloadAsync();   // overwrite with the committed DB values; loop recomputes
                    continue;
                }

                // ENQUEUE level rewards AFTER the level is durably persisted — they land in the player's CLAIMABLE
                // inbox (PlayerRewards), NOT the wallet; the player collects them by tapping. Idempotent per
                // (user,level), so a crash/retry never enqueues twice and a re-climbed level never re-grants.
                if (crossed != null)
                    foreach (var lvl in crossed)
                        await GrantLevelRewardsAsync(userId, lvl, cfg);

                return grantedXp;
            }
        }

        private async Task GrantLevelRewardsAsync(Guid userId, int level, ProgressionConfig cfg)
        {
            try
            {
                var reward = ProgressionMath.LevelUpReward(level, cfg.LvlupBase);
                if (reward > 0)
                    await _rewards.GrantAsync(userId, RewardSource.LevelUp, CurrencyType.Chips, reward,
                        $"Level {level} reward", $"xp:lvlup:{userId}:{level}");

                if (cfg.MilestoneEveryLevels > 0 && level % cfg.MilestoneEveryLevels == 0 && reward > 0)
                    await _rewards.GrantAsync(userId, RewardSource.Milestone, CurrencyType.Chips, reward,
                        $"Level {level} milestone", $"xp:milestone:{userId}:{level}");
            }
            catch (Exception ex)
            {
                // Enqueuing a reward is non-critical — never fail the round (or the XP) over it.
                _logger.LogError(ex, "Level-up reward enqueue failed for user {UserId} level {Level}", userId, level);
            }
        }

        public async Task<ProgressionDto> GetMyProgressionAsync(Guid userId)
        {
            var p = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId);
            if (p == null) return null;

            var now = DateTime.UtcNow;
            var dailyXp = (p.DailyXpResetAt == null || now >= p.DailyXpResetAt) ? 0 : p.DailyXp; // lazy view of the reset
            var cfg = await EffectiveCfgAsync();

            // Normalize the stored (level, into-level XP) against the CURRENT curve before display. Stored values can
            // drift — a curve retune (XpBase/XpExp are admin-tunable), or legacy/seeded data — leaving
            // Experience >= XpToNext(Level) so the bar overfills ("250 / 150"). ApplyLevelUps with 0 fresh XP just
            // carries the excess into the right level. Heal the stored row best-effort (mirrors VIP lazy promotion);
            // any crossed-level reward credits on the next real grant.
            var (xp, level, _) = ProgressionMath.ApplyLevelUps(p.Experience, p.Level, 0, cfg.XpBase, cfg.XpExp);
            if (level != p.Level || xp != p.Experience)
            {
                try
                {
                    await _db.UserProfiles.Where(x => x.UserId == userId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Level, level).SetProperty(x => x.Experience, xp));
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Progression normalize-persist failed for {UserId}", userId); }
            }

            return new ProgressionDto
            {
                Level = level,
                Xp = xp,
                XpToNext = ProgressionMath.XpToNext(level, cfg.XpBase, cfg.XpExp),
                DailyXpRemaining = Math.Max(0, cfg.DailyXpCap - dailyXp),
            };
        }

        /// <summary>Effective config = appsettings base with admin runtime overrides from the Redis "khela:settings"
        /// hash overlaid. Read once per accrual so a saved change applies on the next round; any Redis failure or
        /// unparseable value falls back to the base config, so overrides can never break accrual.</summary>
        private async Task<ProgressionConfig> EffectiveCfgAsync()
        {
            try
            {
                var entries = await _redis.GetDatabase().HashGetAllAsync(SettingsHashKey);
                if (entries == null || entries.Length == 0) return _cfg;
                var map = new Dictionary<string, string>(entries.Length);
                foreach (var e in entries) map[(string)e.Name] = (string)e.Value;
                return ProgressionMath.Overlay(_cfg, map);
            }
            catch { return _cfg; }
        }
    }
}
