using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Progression;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
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
        private const string DailyCapRedisKey = "progression:dailyXpCap";

        private readonly AppDbContext _db;
        private readonly IWalletService _wallet;
        private readonly IRedisService _redis;
        private readonly ILogger<ProgressionService> _logger;
        private readonly ProgressionConfig _cfg;

        public ProgressionService(AppDbContext db, IWalletService wallet, IRedisService redis,
            IConfiguration config, ILogger<ProgressionService> logger)
        {
            _db = db; _wallet = wallet; _redis = redis; _logger = logger;
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
                LvlupBase = config.GetValue("Progression:LvlupBase", 10_000L),
                MilestoneEveryLevels = config.GetValue("Progression:MilestoneEveryLevels", 10),
            };
        }

        public async Task<long> AccrueForRoundAsync(Guid userId, decimal cleanWager, bool win, string roundId)
        {
            if (!_cfg.Enabled || string.IsNullOrEmpty(roundId)) return 0;   // game-extension layer off → no XP

            // Idempotency: the Experience/Level mutation is += (not idempotent), so gate it durably per
            // (round, user). SET-NX FIRST — favour no-double over no-loss (double XP would double the level-up
            // CHIP rewards on a crash). The settle roll-up already runs once per round under bjr:settled:{round};
            // this key protects against a settle retry after that 1h guard lapses.
            var idemKey = $"xpacc:{roundId}:{userId}";
            if (!await _redis.GetDatabase().StringSetAsync(idemKey, "1", TimeSpan.FromDays(30), When.NotExists))
                return 0;

            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile == null) return 0;

            // Apply under OPTIMISTIC CONCURRENCY. UserProfile carries a RowVersion, so a SAME-USER multi-table
            // concurrent settle races this row: one SaveChanges throws DbUpdateConcurrencyException. Reload the
            // latest row and re-apply from its fresh values — safe because level rewards are idempotent per
            // (user,level), so a retry can never double-pay chips. Without this, the conflict would be swallowed
            // AFTER xpacc is set and the round's XP + rewards would be lost forever.
            for (int attempt = 1; ; attempt++)
            {
                var now = DateTime.UtcNow;
                // Daily-cap window — lazy reset on the first accrual after midnight (no background job needed).
                if (profile.DailyXpResetAt == null || now >= profile.DailyXpResetAt)
                {
                    profile.DailyXp = 0;
                    profile.DailyXpResetAt = now.Date.AddDays(1);   // next UTC midnight
                }

                var cap = await GetDailyXpCapAsync();
                var rawXp = ProgressionMath.RawXp(cleanWager, profile.Level, win, _cfg);
                var grantedXp = Math.Min(rawXp, Math.Max(0, cap - profile.DailyXp));   // excess over the cap DISCARDED
                profile.DailyXp += grantedXp;

                List<int> crossed = null;
                if (grantedXp > 0)
                {
                    var (exp, level, cl) = ProgressionMath.ApplyLevelUps(
                        profile.Experience, profile.Level, grantedXp, _cfg.XpBase, _cfg.XpExp);
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

                // Credit rewards AFTER the level is durably persisted. Idempotent per (user,level), so a crash
                // between save and credit self-heals on retry and a re-climbed level never pays twice.
                if (crossed != null)
                    foreach (var lvl in crossed)
                        await CreditLevelRewardsAsync(userId, lvl);

                return grantedXp;
            }
        }

        private async Task CreditLevelRewardsAsync(Guid userId, int level)
        {
            try
            {
                var reward = ProgressionMath.LevelUpReward(level, _cfg.LvlupBase);
                if (reward > 0)
                    await _wallet.CreditAsync(userId.ToString(), CurrencyType.Chips, reward, TransactionType.Bonus,
                        $"xp:lvlup:{userId}:{level}", new WalletContext { Description = $"Level {level} reward" });

                if (_cfg.MilestoneEveryLevels > 0 && level % _cfg.MilestoneEveryLevels == 0 && reward > 0)
                    await _wallet.CreditAsync(userId.ToString(), CurrencyType.Chips, reward, TransactionType.Bonus,
                        $"xp:milestone:{userId}:{level}", new WalletContext { Description = $"Level {level} milestone" });
            }
            catch (Exception ex)
            {
                // A reward credit is a non-critical, idempotent bonus — never fail the round (or the XP) over it.
                _logger.LogError(ex, "Level-up reward credit failed for user {UserId} level {Level}", userId, level);
            }
        }

        public async Task<ProgressionDto> GetMyProgressionAsync(Guid userId)
        {
            var p = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId);
            if (p == null) return null;

            var now = DateTime.UtcNow;
            var dailyXp = (p.DailyXpResetAt == null || now >= p.DailyXpResetAt) ? 0 : p.DailyXp; // lazy view of the reset
            var cap = await GetDailyXpCapAsync();
            return new ProgressionDto
            {
                Level = p.Level,
                Xp = p.Experience,
                XpToNext = ProgressionMath.XpToNext(p.Level, _cfg.XpBase, _cfg.XpExp),
                DailyXpRemaining = Math.Max(0, cap - dailyXp),
            };
        }

        /// <summary>Runtime-tunable daily cap: a Redis override (set by an admin) wins over the config default.</summary>
        private async Task<long> GetDailyXpCapAsync()
        {
            var v = await _redis.GetDatabase().StringGetAsync(DailyCapRedisKey);
            return v.HasValue && long.TryParse(v, out var cap) && cap >= 0 ? cap : _cfg.DailyXpCap;
        }
    }
}
