using Khela.Game.Database;
using Khela.Game.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Khela.Web.Controllers.Api
{
    /// <summary>
    /// Read-only game/economy statistics for the admin dashboard, computed live from the audited wallet ledger
    /// (WalletTransactions joined to Chips PlayerWallets). The ledger is the source of truth, so rounds/wagered
    /// are derived from it rather than the hand tables. JSON API, admin-gated, no writes.
    /// Bets debit the wallet (Amount &lt; 0) → wagered = -sum; wins credit (Amount &gt; 0) → won = +sum.
    /// </summary>
    [ApiController]
    [Route("api/stats/games")]
    [Authorize(Policy = "Admin")]
    [Produces("application/json")]
    public sealed class GameStatsController : ControllerBase
    {
        private readonly AppDbContext _db;

        public GameStatsController(AppDbContext db) => _db = db;

        /// <summary>GET /api/stats/games — rounds + wagered/won over 24h and 7d windows.</summary>
        [HttpGet]
        public async Task<ActionResult<GameStatsDto>> Get()
        {
            var now = DateTime.UtcNow;
            var dayAgo = now.AddDays(-1);
            var weekAgo = now.AddDays(-7);

            // Restrict to Chips wallets (the wagerable play-money currency).
            var chipWalletIds = _db.PlayerWallets
                .Where(w => w.Currency == CurrencyType.Chips)
                .Select(w => w.WalletId);

            var bets24h = _db.WalletTransactions
                .Where(t => t.Type == TransactionType.Bet && t.CreatedAt >= dayAgo && chipWalletIds.Contains(t.WalletId));
            var bets7d = _db.WalletTransactions
                .Where(t => t.Type == TransactionType.Bet && t.CreatedAt >= weekAgo && chipWalletIds.Contains(t.WalletId));
            var wins24h = _db.WalletTransactions
                .Where(t => t.Type == TransactionType.Win && t.CreatedAt >= dayAgo && chipWalletIds.Contains(t.WalletId));

            var wagered24h = -(await bets24h.SumAsync(t => (decimal?)t.Amount) ?? 0m);
            var wagered7d = -(await bets7d.SumAsync(t => (decimal?)t.Amount) ?? 0m);
            var won24h = await wins24h.SumAsync(t => (decimal?)t.Amount) ?? 0m;

            var dto = new GameStatsDto
            {
                Rounds24h       = await bets24h.Where(t => t.RoundId != null && t.RoundId != "").Select(t => t.RoundId).Distinct().CountAsync(),
                Rounds7d        = await bets7d.Where(t => t.RoundId != null && t.RoundId != "").Select(t => t.RoundId).Distinct().CountAsync(),
                BetsPlaced24h   = await bets24h.CountAsync(),
                ChipsWagered24h = wagered24h,
                ChipsWagered7d  = wagered7d,
                ChipsWon24h     = won24h,
                HouseNet24h     = wagered24h - won24h,   // chips the house kept (wagered minus paid out)
                GeneratedAtUtc  = now
            };

            return dto;
        }

        /// <summary>GET /api/stats/games/wagered-series — daily chips wagered for the last 30 days (zero-filled).</summary>
        [HttpGet("wagered-series")]
        public async Task<ActionResult<List<DayPointDto>>> WageredSeries()
        {
            var fromDay = DateTime.UtcNow.Date.AddDays(-29);   // 30 inclusive days, UTC

            var chipWalletIds = _db.PlayerWallets
                .Where(w => w.Currency == CurrencyType.Chips)
                .Select(w => w.WalletId);

            var raw = await _db.WalletTransactions
                .Where(t => t.Type == TransactionType.Bet && t.CreatedAt >= fromDay && chipWalletIds.Contains(t.WalletId))
                .GroupBy(t => t.CreatedAt.Date)
                .Select(g => new { Day = g.Key, Wagered = -g.Sum(x => x.Amount) })
                .ToListAsync();

            var map = raw.ToDictionary(r => r.Day, r => r.Wagered);
            var series = new List<DayPointDto>(30);
            for (int i = 0; i < 30; i++)
            {
                var day = fromDay.AddDays(i);
                series.Add(new DayPointDto { Date = day, Wagered = map.TryGetValue(day, out var w) ? w : 0m });
            }
            return series;
        }
    }

    /// <summary>Game/economy overview returned by <see cref="GameStatsController"/>.</summary>
    public sealed class GameStatsDto
    {
        public int Rounds24h { get; set; }
        public int Rounds7d { get; set; }
        public int BetsPlaced24h { get; set; }
        public decimal ChipsWagered24h { get; set; }
        public decimal ChipsWagered7d { get; set; }
        public decimal ChipsWon24h { get; set; }
        public decimal HouseNet24h { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }

    /// <summary>One day's wagered total for the dashboard time-series chart.</summary>
    public sealed class DayPointDto
    {
        public DateTime Date { get; set; }
        public decimal Wagered { get; set; }
    }
}
