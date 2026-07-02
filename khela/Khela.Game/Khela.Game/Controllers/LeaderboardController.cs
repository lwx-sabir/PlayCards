using System.Security.Claims;
using Khela.Common.Social;                                   // FriendshipStatus
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LbGameType = Khela.Common.Leaderboards.GameType;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Read-only leaderboards, computed straight from SQL — no Redis ZSETs, no seal/instance machinery.
    /// AllTime comes from the running-total tables (UserGameStats per-game, UserProfile cross-game); windowed
    /// (daily/weekly/monthly) is a date-range aggregate over PlayerDailyStat. A board is just
    /// (game, metric, period, scope); the period is a date range. Self-rank is always returned.
    /// Metrics: xp (sum), biggestwin (max), streak (all-time only). game="general" = cross-game.
    /// </summary>
    [ApiController]
    [Route("api/leaderboard")]
    [Authorize]
    public sealed class LeaderboardController : ControllerBase
    {
        private readonly AppDbContext _db;

        public LeaderboardController(AppDbContext db) => _db = db;

        // Featured boards the client/dashboard renders as tabs. Add per-game rows as games ship.
        private static readonly LbBoardDto[] Featured =
        {
            new("general",   "xp",         "weekly",  "Top Players — Weekly"),
            new("general",   "xp",         "alltime", "Top Players — All-Time"),
            new("blackjack", "xp",         "weekly",  "Blackjack — Weekly XP"),
            new("blackjack", "biggestwin", "weekly",  "Blackjack — Biggest Win"),
            new("blackjack", "streak",     "alltime", "Blackjack — Longest Streak"),
        };

        [HttpGet("boards")]
        public ActionResult<IEnumerable<LbBoardDto>> Boards() => Ok(Featured);

        /// <summary>GET /api/leaderboard?game=&amp;metric=&amp;period=&amp;scope=&amp;top= — one board page + the caller's own rank.</summary>
        [HttpGet]
        public async Task<ActionResult<LbPageDto>> Get(
            string game = "general", string metric = "xp", string period = "weekly", string scope = "global", int top = 50)
        {
            top = Math.Clamp(top, 1, 200);
            game = (game ?? "general").Trim().ToLowerInvariant();
            metric = (metric ?? "xp").Trim().ToLowerInvariant();
            period = (period ?? "weekly").Trim().ToLowerInvariant();
            scope = (scope ?? "global").Trim().ToLowerInvariant();

            if (!TryGame(game, out var g)) return BadRequest(new { message = "Unknown game." });
            if (metric is not ("xp" or "biggestwin" or "streak")) return BadRequest(new { message = "Unknown metric." });
            if (metric == "streak") period = "alltime";                  // streaks can't be windowed
            if (period is not ("daily" or "weekly" or "monthly" or "alltime")) return BadRequest(new { message = "Unknown period." });
            if (scope is not ("global" or "friends" or "country")) return BadRequest(new { message = "Unknown scope." });

            var me = CallerId();
            if (me == Guid.Empty) return Unauthorized();

            string region = null;
            HashSet<Guid> friends = null;
            if (scope == "country")
                region = await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == me).Select(p => p.Region).FirstOrDefaultAsync() ?? "ZZ";
            else if (scope == "friends")
                friends = await FriendIdsAsync(me);

            var (ranked, meScore, meRank) = period == "alltime"
                ? await AllTimeAsync(g, metric, scope, region, friends, me, top)
                : await WindowedAsync(g, metric, period, scope, region, friends, me, top);

            // batch-fetch display info for the page + the caller
            var ids = ranked.Select(x => x.UserId).ToList();
            if (meScore.HasValue && !ids.Contains(me)) ids.Add(me);
            var profs = (await _db.UserProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId))
                            .Select(p => new { p.UserId, p.DisplayName, p.AvatarId, p.Region }).ToListAsync())
                        .ToDictionary(p => p.UserId);

            var entries = new List<LbEntryDto>(ranked.Count);
            for (int i = 0; i < ranked.Count; i++)
            {
                profs.TryGetValue(ranked[i].UserId, out var pr);
                entries.Add(new LbEntryDto(i + 1, ranked[i].UserId.ToString(),
                    pr?.DisplayName ?? "Player", pr?.AvatarId, pr?.Region ?? "ZZ", ranked[i].Score));
            }

            LbEntryDto meEntry = null;
            if (meScore.HasValue && meRank.HasValue)
            {
                profs.TryGetValue(me, out var pr);
                meEntry = new LbEntryDto(meRank.Value, me.ToString(), pr?.DisplayName ?? "You", pr?.AvatarId, pr?.Region ?? "ZZ", meScore.Value);
            }

            return new LbPageDto(game, metric, period, scope, entries, meEntry);
        }

        // ---- all-time: running totals (UserGameStats per-game / UserProfile cross-game) ----
        private async Task<(List<(Guid UserId, decimal Score)>, decimal?, int?)> AllTimeAsync(
            LbGameType g, string metric, string scope, string region, HashSet<Guid> friends, Guid me, int top)
        {
            IQueryable<ScoreRow> sel;
            if (g == LbGameType.General)
            {
                var q = _db.UserProfiles.AsNoTracking();
                if (scope == "country") q = q.Where(p => p.Region == region);
                else if (scope == "friends") q = q.Where(p => friends.Contains(p.UserId));
                sel = metric switch
                {
                    "biggestwin" => q.Select(p => new ScoreRow { UserId = p.UserId, Score = p.BiggestWin }),
                    "streak"     => q.Select(p => new ScoreRow { UserId = p.UserId, Score = (decimal)p.LongestWinStreak }),
                    _            => q.Select(p => new ScoreRow { UserId = p.UserId, Score = (decimal)p.LifetimeExperience }),
                };
            }
            else
            {
                var q = _db.UserGameStats.AsNoTracking().Where(s => s.GameType == g);
                if (scope == "country") q = q.Where(s => s.Region == region);
                else if (scope == "friends") q = q.Where(s => friends.Contains(s.UserId));
                sel = metric switch
                {
                    "biggestwin" => q.Select(s => new ScoreRow { UserId = s.UserId, Score = s.BiggestSingleWin }),
                    "streak"     => q.Select(s => new ScoreRow { UserId = s.UserId, Score = (decimal)s.LongestWinStreak }),
                    _            => q.Select(s => new ScoreRow { UserId = s.UserId, Score = (decimal)s.ExperienceEarned }),
                };
            }
            return await RankAsync(sel, me, top);
        }

        // ---- windowed: date-range aggregate over PlayerDailyStat ----
        private async Task<(List<(Guid UserId, decimal Score)>, decimal?, int?)> WindowedAsync(
            LbGameType g, string metric, string period, string scope, string region, HashSet<Guid> friends, Guid me, int top)
        {
            var today = DateTime.UtcNow.Date;
            var from = period switch
            {
                "daily"   => today,
                "monthly" => new DateTime(today.Year, today.Month, 1),
                _         => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),   // weekly: Monday of this week (UTC)
            };

            var q = _db.PlayerDailyStats.AsNoTracking().Where(d => d.StatDate >= from && d.StatDate <= today);
            if (g != LbGameType.General) q = q.Where(d => d.GameType == g);
            if (scope == "country") q = q.Where(d => d.Region == region);
            else if (scope == "friends") q = q.Where(d => friends.Contains(d.UserId));

            IQueryable<ScoreRow> grouped = metric == "biggestwin"
                ? q.GroupBy(d => d.UserId).Select(grp => new ScoreRow { UserId = grp.Key, Score = grp.Max(x => x.BiggestSingleWin) })
                : q.GroupBy(d => d.UserId).Select(grp => new ScoreRow { UserId = grp.Key, Score = (decimal)grp.Sum(x => x.Xp) });

            return await RankAsync(grouped, me, top);
        }

        // Shared: top-N + caller's score + caller's rank (count-above + 1), over any (UserId, Score) projection.
        private static async Task<(List<(Guid UserId, decimal Score)>, decimal?, int?)> RankAsync(
            IQueryable<ScoreRow> sel, Guid me, int top)
        {
            var rows = await sel.OrderByDescending(r => r.Score)
                .Take(top).Select(r => new { r.UserId, r.Score }).ToListAsync();
            var ranked = rows.Select(r => (r.UserId, r.Score)).ToList();

            var meScore = await sel.Where(r => r.UserId == me).Select(r => (decimal?)r.Score).FirstOrDefaultAsync();
            int? meRank = meScore.HasValue ? await sel.CountAsync(r => r.Score > meScore.Value) + 1 : null;
            return (ranked, meScore, meRank);
        }

        private async Task<HashSet<Guid>> FriendIdsAsync(Guid me)
        {
            var ids = await _db.Friendships.AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == me || f.AddresseeId == me))
                .Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId)
                .ToListAsync();
            return new HashSet<Guid>(ids) { me };   // include self so you appear on your friends board
        }

        private Guid CallerId()
        {
            var s = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            return Guid.TryParse(s, out var g) ? g : Guid.Empty;
        }

        private static bool TryGame(string s, out LbGameType g)
        {
            switch (s)
            {
                case "general":   g = LbGameType.General;   return true;
                case "blackjack": g = LbGameType.Blackjack; return true;
                case "poker":     g = LbGameType.Poker;     return true;
                case "teenpatti": g = LbGameType.TeenPatti; return true;
                case "roulette":  g = LbGameType.Roulette;  return true;
                default:          g = LbGameType.General;   return false;
            }
        }

        private sealed class ScoreRow { public Guid UserId { get; set; } public decimal Score { get; set; } }
    }

    public sealed record LbEntryDto(int Rank, string UserId, string DisplayName, string AvatarId, string Region, decimal Score);
    public sealed record LbPageDto(string Game, string Metric, string Period, string Scope, List<LbEntryDto> Entries, LbEntryDto Me);
    public sealed record LbBoardDto(string Game, string Metric, string Period, string DisplayName);
}
