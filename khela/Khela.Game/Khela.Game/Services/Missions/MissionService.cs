using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Chests;
using Khela.Common.Missions;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using GameType = Khela.Common.Leaderboards.GameType;   // the LEADERBOARD GameType (matches UserGameStats), not the ledger enum

namespace Khela.Game.Services.Missions
{
    public interface IMissionService
    {
        /// <summary>The caller's daily missions (lazily assigned for today) + the bundle state + reset time.</summary>
        Task<DailyMissionsDto> GetDailyAsync(Guid userId);

        /// <summary>Advance active daily missions from a settled round's events (round count, clean wager, hand
        /// outcomes from the stat counters). Idempotent per (round, user) — rides the settle roll-up.</summary>
        Task ReportRoundAsync(Guid userId, IReadOnlyDictionary<string, long> statCounters, decimal cleanWager, string roundId);

        /// <summary>Claim a completed mission — the reward is credited STRAIGHT TO BALANCE, idempotent on the row id.</summary>
        Task<MissionClaimResultDto> ClaimAsync(Guid userId, Guid missionRowId);

        /// <summary>Claim the "complete ALL daily missions" bundle (once per UTC day).</summary>
        Task<MissionClaimResultDto> ClaimBundleAsync(Guid userId);
    }

    /// <summary>
    /// Server-authoritative daily missions. The pool/counts/bundle come from the EFFECTIVE config — the admin override
    /// JSON in Redis <c>khela:missions</c> if present, else <see cref="MissionCatalog.Defaults"/> — so missions are
    /// editable from the dashboard with no redeploy. Each player is lazily assigned a random few per UTC day (by game +
    /// difficulty); progress advances at settle (idempotent per round, from the same events the stat counters use);
    /// rewards are credited via the idempotent <see cref="IWalletService"/> on claim.
    /// </summary>
    public sealed class MissionService : IMissionService
    {
        // Games eligible for daily missions. (Later: derive from the available GameDefinitions / GameCatalog.)
        private static readonly GameType[] AvailableGames = { GameType.Blackjack };

        private readonly AppDbContext _db;
        private readonly IWalletService _wallet;
        private readonly IRedisService _redis;
        private readonly IChestService _chests;
        private readonly ILogger<MissionService> _logger;

        public MissionService(AppDbContext db, IWalletService wallet, IRedisService redis, IChestService chests, ILogger<MissionService> logger)
        {
            _db = db; _wallet = wallet; _redis = redis; _chests = chests; _logger = logger;
        }

        public async Task<DailyMissionsDto> GetDailyAsync(Guid userId)
        {
            var cfg = await EffectiveAsync();
            var today = DateTime.UtcNow.Date;
            var rows = await EnsureAssignedAsync(userId, today, cfg);

            var dto = new DailyMissionsDto { ResetAtUtc = today.AddDays(1) };
            foreach (var r in rows)
            {
                var def = cfg.Find(r.MissionId);
                if (def == null) continue;
                dto.Missions.Add(new MissionDto
                {
                    Id = r.Id.ToString(),
                    MissionId = r.MissionId,
                    Type = (int)def.Type,
                    Difficulty = (int)def.Difficulty,
                    Title = def.Title,
                    Description = def.Description,
                    IconKey = def.IconKey,
                    Progress = r.Progress,
                    Target = def.Target,
                    Status = (int)r.Status,
                    RewardCurrency = (int)def.RewardCurrency,
                    RewardAmount = def.RewardAmount,
                });
            }

            dto.BundleChestType = cfg.Bundle.Key;
            dto.BundleChestTier = cfg.Bundle.Tier.ToString();
            var bundleChest = (await _chests.ListAsync()).FirstOrDefault(c =>
                string.Equals(c.Key, cfg.Bundle.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Tier, cfg.Bundle.Tier.ToString(), StringComparison.OrdinalIgnoreCase));
            dto.BundleChestTitle = bundleChest?.Title;
            dto.BundleChestDescription = bundleChest?.Description;
            bool allDone = rows.Count > 0 && rows.All(r => r.Status == MissionStatus.Completed || r.Status == MissionStatus.Claimed);
            bool bundleClaimed = await _db.PlayerDailyMissionBundles.AsNoTracking().AnyAsync(b => b.UserId == userId && b.AssignedDate == today);
            dto.BundleClaimed = bundleClaimed;
            dto.BundleClaimable = allDone && !bundleClaimed;
            return dto;
        }

        public async Task ReportRoundAsync(Guid userId, IReadOnlyDictionary<string, long> statCounters, decimal cleanWager, string roundId)
        {
            if (string.IsNullOrEmpty(roundId)) return;
            // Idempotent per (round, user) — a re-run would double-count progress.
            if (!await _redis.GetDatabase().StringSetAsync($"missionacc:{roundId}:{userId}", "1", TimeSpan.FromDays(2), When.NotExists))
                return;

            var today = DateTime.UtcNow.Date;
            var rows = await _db.PlayerDailyMissions
                .Where(m => m.UserId == userId && m.AssignedDate == today && m.Status == MissionStatus.Active)
                .ToListAsync();
            if (rows.Count == 0) return;

            var cfg = await EffectiveAsync();
            long Ctr(string k) => statCounters != null && statCounters.TryGetValue(k, out var v) ? v : 0L;
            var deltas = new Dictionary<MissionType, long>
            {
                [MissionType.PlayRounds]    = 1,
                [MissionType.WinHands]      = Ctr("handsWon"),
                [MissionType.WagerChips]    = (long)cleanWager,
                [MissionType.GetBlackjacks] = Ctr("blackjacks"),
                [MissionType.Doubles]       = Ctr("doubles"),
                [MissionType.Splits]        = Ctr("splits"),
                [MissionType.Pushes]        = Ctr("pushes"),
            };

            var now = DateTime.UtcNow;
            foreach (var r in rows)
            {
                var def = cfg.Find(r.MissionId);
                if (def == null) continue;
                if (!deltas.TryGetValue(def.Type, out var delta) || delta <= 0) continue;
                r.Progress = Math.Min(def.Target, r.Progress + delta);
                if (r.Progress >= def.Target) r.Status = MissionStatus.Completed;
                r.UpdatedAt = now;
            }
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); /* a concurrent write won; next round reconciles */ }
        }

        public async Task<MissionClaimResultDto> ClaimAsync(Guid userId, Guid missionRowId)
        {
            var row = await _db.PlayerDailyMissions.FirstOrDefaultAsync(m => m.Id == missionRowId && m.UserId == userId);
            if (row == null) return Fail("Mission not found.");
            var cfg = await EffectiveAsync();
            var def = cfg.Find(row.MissionId);
            if (def == null) return Fail("Unknown mission.");
            if (row.Status == MissionStatus.Claimed) return new MissionClaimResultDto { Ok = true, ClaimedCount = 0, NewChipBalance = await ChipsAsync(userId) };
            if (row.Progress < def.Target) return Fail("Mission not complete yet.");

            // Credit STRAIGHT TO BALANCE, idempotent on the row id (a double-tap can't double-pay).
            await _wallet.CreditAsync(userId.ToString(), def.RewardCurrency, def.RewardAmount, TransactionType.Bonus,
                $"mission:{row.Id:N}", new WalletContext { Description = $"Mission: {def.Title}" });
            row.Status = MissionStatus.Claimed;
            row.ClaimedAt = DateTime.UtcNow;
            try { await _db.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }

            return new MissionClaimResultDto { Ok = true, ClaimedCount = 1, NewChipBalance = await ChipsAsync(userId) };
        }

        public async Task<MissionClaimResultDto> ClaimBundleAsync(Guid userId)
        {
            var today = DateTime.UtcNow.Date;
            if (await _db.PlayerDailyMissionBundles.AnyAsync(b => b.UserId == userId && b.AssignedDate == today))
                return new MissionClaimResultDto { Ok = true, ClaimedCount = 0, NewChipBalance = await ChipsAsync(userId) };

            var statuses = await _db.PlayerDailyMissions.AsNoTracking()
                .Where(m => m.UserId == userId && m.AssignedDate == today).Select(m => m.Status).ToListAsync();
            if (statuses.Count == 0) return Fail("No missions today.");
            if (!statuses.All(s => s == MissionStatus.Completed || s == MissionStatus.Claimed)) return Fail("Complete all missions first.");

            // Open the bundle CHEST (rolled + credited idempotently per (user, day)), then record the claim marker.
            var cfg = await EffectiveAsync();
            var date = today.ToString("yyyyMMdd");
            var chest = await _chests.OpenAsync(userId, cfg.Bundle.Key, cfg.Bundle.Tier, $"mission-bundle:{userId:N}:{date}");
            // If the configured chest is missing/misconfigured, DON'T burn the claim — let the player retry once an admin fixes it.
            if (chest == null || !chest.Ok) return Fail(chest?.Error ?? "Bundle reward is misconfigured.");

            _db.PlayerDailyMissionBundles.Add(new PlayerDailyMissionBundle { UserId = userId, AssignedDate = today });
            try { await _db.SaveChangesAsync(); } catch (DbUpdateException) { _db.ChangeTracker.Clear(); /* concurrent claim won the unique (user,day) — chest open was idempotent */ }

            return new MissionClaimResultDto
            {
                Ok = true,
                ClaimedCount = 1,
                NewChipBalance = chest.NewChipBalance,
                Rewards = chest.Rewards ?? new List<RolledRewardDto>(),
            };
        }

        // ---- helpers ----

        /// <summary>Effective config = admin override (Redis khela:missions JSON) ?? code defaults. Read per call
        /// (small JSON) so a dashboard save applies on the next fetch/round with no restart.</summary>
        private async Task<MissionConfig> EffectiveAsync()
        {
            try
            {
                var json = await _redis.GetDatabase().StringGetAsync(MissionCatalog.RedisKey);
                if (json.HasValue)
                {
                    var cfg = MissionCatalog.TryParse(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { /* Redis down / bad override → fall through to defaults */ }
            return MissionCatalog.Defaults();
        }

        private async Task<List<PlayerDailyMission>> EnsureAssignedAsync(Guid userId, DateTime today, MissionConfig cfg)
        {
            var rows = await _db.PlayerDailyMissions.Where(m => m.UserId == userId && m.AssignedDate == today).ToListAsync();
            if (rows.Count > 0) return rows;

            var picks = new List<MissionDef>();
            picks.AddRange(PickRandom(cfg.Pool(MissionDifficulty.Easy, AvailableGames), cfg.DailyCount(MissionDifficulty.Easy)));
            picks.AddRange(PickRandom(cfg.Pool(MissionDifficulty.Medium, AvailableGames), cfg.DailyCount(MissionDifficulty.Medium)));
            picks.AddRange(PickRandom(cfg.Pool(MissionDifficulty.Hard, AvailableGames), cfg.DailyCount(MissionDifficulty.Hard)));

            var now = DateTime.UtcNow;
            foreach (var def in picks)
                _db.PlayerDailyMissions.Add(new PlayerDailyMission { UserId = userId, MissionId = def.Id, AssignedDate = today, Status = MissionStatus.Active, CreatedAt = now });
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateException) { _db.ChangeTracker.Clear(); /* concurrent generation hit the unique (user,mission,day) — fall through to reload */ }

            return await _db.PlayerDailyMissions.Where(m => m.UserId == userId && m.AssignedDate == today).ToListAsync();
        }

        private static IEnumerable<MissionDef> PickRandom(List<MissionDef> pool, int n)
            => pool.OrderBy(_ => Random.Shared.Next()).Take(n);

        private async Task<decimal> ChipsAsync(Guid userId) => await _wallet.GetBalanceAsync(userId.ToString(), CurrencyType.Chips);

        private static MissionClaimResultDto Fail(string error) => new MissionClaimResultDto { Ok = false, Error = error };
    }
}
