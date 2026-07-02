using Khela.Common.Leaderboards;   // VipTier
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Khela.Web.Controllers.Api
{
    /// <summary>
    /// Read-only player statistics for the admin dashboard, computed live from the shared Khela database
    /// (UserProfiles + PlayerWallets). Pure JSON API (consumed by the dashboard via fetch), admin-gated like
    /// the rest of the console. No writes — only aggregate reads — so there are no wallet/idempotency concerns.
    /// </summary>
    [ApiController]
    [Route("api/stats/users")]
    [Authorize(Policy = "Admin")]
    [Produces("application/json")]
    public sealed class UserStatsController : ControllerBase
    {
        private readonly AppDbContext _db;

        public UserStatsController(AppDbContext db) => _db = db;

        /// <summary>GET /api/stats/users — overview counts + level / region / VIP breakdown.</summary>
        [HttpGet]
        public async Task<ActionResult<UserStatsDto>> Get()
        {
            var now = DateTime.UtcNow;
            var todayStart = now.Date;          // UTC midnight
            var dayAgo = now.AddDays(-1);
            var weekAgo = now.AddDays(-7);

            var profiles = _db.UserProfiles.AsNoTracking();
            var total = await profiles.CountAsync();

            var dto = new UserStatsDto
            {
                TotalPlayers   = total,
                NewToday       = await profiles.CountAsync(p => p.CreatedAt >= todayStart),
                NewThisWeek    = await profiles.CountAsync(p => p.CreatedAt >= weekAgo),
                ActiveToday    = await profiles.CountAsync(p => p.LastSeenAt != null && p.LastSeenAt >= dayAgo),
                ActiveThisWeek = await profiles.CountAsync(p => p.LastSeenAt != null && p.LastSeenAt >= weekAgo),
                MaxLevel       = total == 0 ? 0 : await profiles.MaxAsync(p => p.Level),
                AvgLevel       = total == 0 ? 0 : Math.Round(await profiles.AverageAsync(p => (double)p.Level), 1),
                VipPlayers     = await profiles.CountAsync(p => p.VipTier != VipTier.None),

                // Chips currently held across all players (the wagerable play-money float — read-only sum).
                ChipsInCirculation = await _db.PlayerWallets
                    .Where(w => w.Currency == CurrencyType.Chips)
                    .SumAsync(w => (decimal?)w.Balance) ?? 0m,

                TopRegions = await profiles
                    .GroupBy(p => p.Region)
                    .Select(g => new RegionCountDto { Region = g.Key, Count = g.Count() })
                    .OrderByDescending(r => r.Count)
                    .Take(6)
                    .ToListAsync(),

                GeneratedAtUtc = now
            };

            return dto;
        }

        /// <summary>GET /api/stats/users/recent — the most recently registered players, with their Chips balance.</summary>
        [HttpGet("recent")]
        public async Task<ActionResult<List<RecentPlayerDto>>> Recent()
        {
            var weekAgo = DateTime.UtcNow.AddDays(-7);
            var list = await _db.UserProfiles.AsNoTracking()
                .Where(p => !p.DisplayName.StartsWith("smk"))   // hide smoke-test accounts from the view (they stay in the DB)
                .OrderByDescending(p => p.CreatedAt)
                .Take(8)
                .Select(p => new RecentPlayerDto
                {
                    DisplayName = p.DisplayName,
                    Level = p.Level,
                    Region = p.Region == "ZZ" ? "—" : p.Region,
                    Active = p.LastSeenAt != null && p.LastSeenAt >= weekAgo,
                    Chips = _db.PlayerWallets
                        .Where(w => w.UserId == p.UserId && w.Currency == CurrencyType.Chips)
                        .Select(w => (decimal?)w.Balance).FirstOrDefault() ?? 0m
                })
                .ToListAsync();
            return list;
        }
    }

    /// <summary>Player-statistics overview returned by <see cref="UserStatsController"/>.</summary>
    public sealed class UserStatsDto
    {
        public int TotalPlayers { get; set; }
        public int NewToday { get; set; }
        public int NewThisWeek { get; set; }
        public int ActiveToday { get; set; }
        public int ActiveThisWeek { get; set; }
        public int MaxLevel { get; set; }
        public double AvgLevel { get; set; }
        public int VipPlayers { get; set; }
        public decimal ChipsInCirculation { get; set; }
        public List<RegionCountDto> TopRegions { get; set; } = new();
        public DateTime GeneratedAtUtc { get; set; }
    }

    public sealed class RegionCountDto
    {
        public string Region { get; set; } = "";
        public int Count { get; set; }
    }

    /// <summary>A recently-registered player row for the dashboard table.</summary>
    public sealed class RecentPlayerDto
    {
        public string DisplayName { get; set; } = "";
        public int Level { get; set; }
        public string Region { get; set; } = "";
        public bool Active { get; set; }
        public decimal Chips { get; set; }
    }
}
