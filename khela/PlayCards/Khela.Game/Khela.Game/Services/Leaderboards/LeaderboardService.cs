using System.Globalization;
using LbGameType = Khela.Common.Leaderboards.GameType;
using Khela.Common.Leaderboards;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Khela.Game.Services.Leaderboards
{
    /// <summary>Per-round metric deltas pushed to the live leaderboard ZSETs.</summary>
    public readonly record struct RoundMetrics(
        decimal ChipsWon, decimal NetProfit, decimal TotalWagered,
        long Experience, int RoundsWon, decimal BiggestWin, int LongestWinStreak, int GamesPlayed);

    public sealed record LeaderboardEntryDto(int Rank, string UserId, string DisplayName, string AvatarId, double Score);
    public sealed record LeaderboardBoardDto(string Code, string Name, int GameType, int Metric, int Period, int Scope);
    public sealed record LeaderboardPageDto(string Code, string Name, string PeriodKey, string RegionKey,
        IReadOnlyList<LeaderboardEntryDto> Top, LeaderboardEntryDto Me);

    public interface ILeaderboardService
    {
        Task RecordRoundAsync(Guid userId, LbGameType gameType, string region, RoundMetrics metrics);
        Task<IReadOnlyList<LeaderboardBoardDto>> GetActiveBoardsAsync();
        Task<LeaderboardPageDto> GetPageAsync(string code, LeaderboardScope scope, string region, Guid? meUserId, int count = 50);
        Task SeedAsync();
    }

    /// <summary>
    /// Live leaderboards. Redis sorted sets own the hot scores (key lb:{def}:{periodKey}:{regionKey});
    /// MySQL holds the config (LeaderboardDefinition), window instances, and seasons. Scores are written
    /// from the settle path via <see cref="RecordRoundAsync"/>; reads come straight off Redis.
    /// Seeding + the seal/payout job are out of the live path. (Seal job = TODO.)
    /// </summary>
    public sealed class LeaderboardService : ILeaderboardService
    {
        private const string ActiveDefsCacheKey = "lb:active-defs";

        private readonly AppDbContext _db;
        private readonly IRedisService _redis;
        private readonly IMemoryCache _cache;

        public LeaderboardService(AppDbContext db, IRedisService redis)
        {
            _db = db;
            _redis = redis;
            _cache = redis.GetMemoryCache();
        }

        // ---------- write (called on settle) ----------
        public async Task RecordRoundAsync(Guid userId, LbGameType gameType, string region, RoundMetrics m)
        {
            var defs = await GetActiveDefsAsync();
            var applicable = defs.Where(d => d.GameType == gameType || d.GameType == LbGameType.General).ToList();
            if (applicable.Count == 0) return;

            var season = await GetActiveSeasonAsync();
            var rdb = _redis.GetDatabase();

            foreach (var def in applicable)
            {
                var value = MetricValue(def.Metric, m);
                if (def.Aggregation == MetricAggregation.Sum && value == 0m) continue; // nothing to add

                var periodKey = ComputePeriodKey(def.Period, season);
                var regionKey = def.Scope == LeaderboardScope.Regional ? Norm(region) : "GLOBAL";

                await EnsureInstanceAsync(def, periodKey, regionKey, season);

                var key = RedisKey(def.DefinitionId, periodKey, regionKey);
                var member = userId.ToString();

                if (def.Aggregation == MetricAggregation.Sum)
                {
                    await rdb.SortedSetIncrementAsync(key, member, (double)value);
                }
                else // Max: keep the greater (e.g. biggest win / longest streak)
                {
                    var current = await rdb.SortedSetScoreAsync(key, member);
                    if (!current.HasValue || (double)value > current.Value)
                        await rdb.SortedSetAddAsync(key, member, (double)value);
                }
            }
        }

        // ---------- read ----------
        public async Task<IReadOnlyList<LeaderboardBoardDto>> GetActiveBoardsAsync()
        {
            var defs = await GetActiveDefsAsync();
            return defs
                .Select(d => new LeaderboardBoardDto(d.Code, d.DisplayName, (int)d.GameType, (int)d.Metric, (int)d.Period, (int)d.Scope))
                .ToList();
        }

        public async Task<LeaderboardPageDto> GetPageAsync(string code, LeaderboardScope scope, string region, Guid? meUserId, int count = 50)
        {
            var def = (await GetActiveDefsAsync()).FirstOrDefault(d => d.Code == code);
            if (def == null) return null;

            var season = await GetActiveSeasonAsync();
            var periodKey = ComputePeriodKey(def.Period, season);
            var regionKey = def.Scope == LeaderboardScope.Regional ? Norm(region) : "GLOBAL";
            var key = RedisKey(def.DefinitionId, periodKey, regionKey);
            var rdb = _redis.GetDatabase();

            count = Math.Clamp(count, 1, 200);
            var raw = await rdb.SortedSetRangeByRankWithScoresAsync(key, 0, count - 1, Order.Descending);

            var ids = raw.Select(e => Guid.TryParse(e.Element, out var g) ? g : Guid.Empty)
                         .Where(g => g != Guid.Empty).ToList();
            var byId = (await _db.UserProfiles.Where(p => ids.Contains(p.UserId))
                            .Select(p => new { p.UserId, p.DisplayName, p.AvatarId }).ToListAsync())
                        .ToDictionary(p => p.UserId);

            var top = new List<LeaderboardEntryDto>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                Guid.TryParse(raw[i].Element, out var g);
                byId.TryGetValue(g, out var prof);
                top.Add(new LeaderboardEntryDto(i + 1, g.ToString(), prof?.DisplayName ?? "Player", prof?.AvatarId, raw[i].Score));
            }

            LeaderboardEntryDto me = null;
            if (meUserId.HasValue)
            {
                var mem = meUserId.Value.ToString();
                var rank = await rdb.SortedSetRankAsync(key, mem, Order.Descending);
                var score = await rdb.SortedSetScoreAsync(key, mem);
                if (rank.HasValue && score.HasValue)
                {
                    var mp = await _db.UserProfiles.Where(p => p.UserId == meUserId.Value)
                        .Select(p => new { p.DisplayName, p.AvatarId }).FirstOrDefaultAsync();
                    me = new LeaderboardEntryDto((int)rank.Value + 1, mem, mp?.DisplayName ?? "Player", mp?.AvatarId, score.Value);
                }
            }

            return new LeaderboardPageDto(def.Code, def.DisplayName, periodKey, regionKey, top, me);
        }

        // ---------- seeding ----------
        public async Task SeedAsync()
        {
            if (!await _db.LeaderboardSeasons.AnyAsync())
            {
                var now = DateTime.UtcNow;
                var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                _db.LeaderboardSeasons.Add(new LeaderboardSeason
                {
                    SeasonNumber = 1,
                    SeasonKey = "S1",
                    Name = "Season 1",
                    StartsAtUtc = start,
                    EndsAtUtc = start.AddMonths(3),
                    IsActive = true
                });
            }

            var seed = new[]
            {
                Def("bj_chips_alltime", "Blackjack — Most Chips Won (All-Time)", LbGameType.Blackjack, LeaderboardMetric.ChipsWon, MetricAggregation.Sum, LeaderboardPeriod.AllTime, LeaderboardScope.Global),
                Def("bj_chips_weekly",  "Blackjack — Chips Won (Weekly)",        LbGameType.Blackjack, LeaderboardMetric.ChipsWon, MetricAggregation.Sum, LeaderboardPeriod.Weekly,  LeaderboardScope.Global),
                Def("bj_net_weekly",    "Blackjack — Net Profit (Weekly)",       LbGameType.Blackjack, LeaderboardMetric.NetProfit, MetricAggregation.Sum, LeaderboardPeriod.Weekly, LeaderboardScope.Global),
                Def("all_xp_alltime",   "Top Players (All-Time XP)",                  LbGameType.General,   LeaderboardMetric.Experience, MetricAggregation.Sum, LeaderboardPeriod.AllTime, LeaderboardScope.Global),
                Def("all_xp_weekly",    "Rising Stars (Weekly XP)",                   LbGameType.General,   LeaderboardMetric.Experience, MetricAggregation.Sum, LeaderboardPeriod.Weekly,  LeaderboardScope.Global),
            };
            var existing = await _db.LeaderboardDefinitions.Select(d => d.Code).ToListAsync();
            var toAdd = seed.Where(d => !existing.Contains(d.Code)).ToList();
            if (toAdd.Count > 0) _db.LeaderboardDefinitions.AddRange(toAdd);

            if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync();
            _cache.Remove(ActiveDefsCacheKey);
        }

        private static LeaderboardDefinition Def(string code, string name, LbGameType game, LeaderboardMetric metric,
            MetricAggregation agg, LeaderboardPeriod period, LeaderboardScope scope) => new()
        {
            Code = code,
            DisplayName = name,
            GameType = game,
            Metric = metric,
            Aggregation = agg,
            Period = period,
            Scope = scope,
            IsActive = true
        };

        // ---------- helpers ----------
        private async Task<List<LeaderboardDefinition>> GetActiveDefsAsync()
        {
            if (_cache.TryGetValue(ActiveDefsCacheKey, out List<LeaderboardDefinition> cached) && cached != null)
                return cached;

            var defs = await _db.LeaderboardDefinitions.Where(d => d.IsActive).AsNoTracking().ToListAsync();
            _cache.Set(ActiveDefsCacheKey, defs, TimeSpan.FromMinutes(5));
            return defs;
        }

        private Task<LeaderboardSeason> GetActiveSeasonAsync()
            => _db.LeaderboardSeasons.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);

        private async Task EnsureInstanceAsync(LeaderboardDefinition def, string periodKey, string regionKey, LeaderboardSeason season)
        {
            var exists = await _db.LeaderboardInstances
                .AnyAsync(i => i.DefinitionId == def.DefinitionId && i.PeriodKey == periodKey && i.RegionKey == regionKey);
            if (exists) return;

            var (opens, closes) = Window(def.Period, DateTime.UtcNow, season);
            _db.LeaderboardInstances.Add(new LeaderboardInstance
            {
                DefinitionId = def.DefinitionId,
                PeriodKey = periodKey,
                RegionKey = regionKey,
                GameType = def.GameType,
                OpensAt = opens,
                ClosesAt = closes
            });
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateException) { _db.ChangeTracker.Clear(); } // race: another writer created it — fine
        }

        private static string RedisKey(Guid defId, string periodKey, string regionKey) => $"lb:{defId}:{periodKey}:{regionKey}";

        private static string Norm(string region) => string.IsNullOrWhiteSpace(region) ? "ZZ" : region.Trim().ToUpperInvariant();

        private static decimal MetricValue(LeaderboardMetric metric, RoundMetrics m) => metric switch
        {
            LeaderboardMetric.ChipsWon => m.ChipsWon,
            LeaderboardMetric.NetProfit => m.NetProfit,
            LeaderboardMetric.Experience => m.Experience,
            LeaderboardMetric.RoundsWon => m.RoundsWon,
            LeaderboardMetric.BiggestWin => m.BiggestWin,
            LeaderboardMetric.LongestWinStreak => m.LongestWinStreak,
            LeaderboardMetric.TotalWagered => m.TotalWagered,
            LeaderboardMetric.GamesPlayed => m.GamesPlayed,
            _ => 0m
        };

        private static string ComputePeriodKey(LeaderboardPeriod period, LeaderboardSeason season)
        {
            var now = DateTime.UtcNow;
            return period switch
            {
                LeaderboardPeriod.AllTime => "ALL",
                LeaderboardPeriod.Monthly => now.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                LeaderboardPeriod.Weekly => $"{ISOWeek.GetYear(now):D4}-W{ISOWeek.GetWeekOfYear(now):D2}",
                LeaderboardPeriod.Daily => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                LeaderboardPeriod.Season => season?.SeasonKey ?? "S0",
                _ => "ALL"
            };
        }

        private static (DateTime opens, DateTime? closes) Window(LeaderboardPeriod period, DateTime now, LeaderboardSeason season)
        {
            switch (period)
            {
                case LeaderboardPeriod.Monthly:
                    var m0 = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                    return (m0, m0.AddMonths(1));
                case LeaderboardPeriod.Weekly:
                    int daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
                    var monday = DateTime.SpecifyKind(now.Date.AddDays(-daysSinceMonday), DateTimeKind.Utc);
                    return (monday, monday.AddDays(7));
                case LeaderboardPeriod.Daily:
                    var d0 = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
                    return (d0, d0.AddDays(1));
                case LeaderboardPeriod.Season:
                    return (season?.StartsAtUtc ?? now, season?.EndsAtUtc);
                default: // AllTime
                    return (new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);
            }
        }
    }
}
