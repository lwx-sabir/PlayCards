using Khela.Common.Reports;        // ReportStatus
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Khela.Web.Controllers.Api
{
    /// <summary>
    /// Live system-health alerts for the admin dashboard, derived from REAL operational signals — failed/stuck
    /// wallet transactions (money-path health), the player-report queue, and Redis connectivity. Read-only,
    /// admin-gated. Most-severe first; emits a healthy baseline line when nothing is critical so the panel is
    /// never empty.
    /// </summary>
    [ApiController]
    [Route("api/stats/alerts")]
    [Authorize(Policy = "Admin")]
    [Produces("application/json")]
    public sealed class AlertsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConnectionMultiplexer _redis;

        public AlertsController(AppDbContext db, IConnectionMultiplexer redis)
        {
            _db = db;
            _redis = redis;
        }

        /// <summary>GET /api/stats/alerts — operational alerts, severity = crit | warn | ok.</summary>
        [HttpGet]
        public async Task<ActionResult<List<AlertDto>>> Get()
        {
            var now = DateTime.UtcNow;
            var dayAgo = now.AddDays(-1);

            var failed24h = await _db.WalletTransactions
                .CountAsync(t => t.Status == TransactionStatus.Failed && t.CreatedAt >= dayAgo);
            var stuckPending = await _db.WalletTransactions
                .CountAsync(t => t.Status == TransactionStatus.Pending && t.CreatedAt < now.AddMinutes(-5));
            var openReports = await _db.Reports
                .CountAsync(r => r.Status == ReportStatus.Open);

            bool redisOk;
            try { redisOk = _redis.IsConnected; } catch { redisOk = false; }

            var alerts = new List<AlertDto>();

            if (failed24h > 0)
                alerts.Add(new AlertDto("crit", "Failed transactions", $"{failed24h} wallet transaction(s) failed in the last 24h", "24h"));
            if (!redisOk)
                alerts.Add(new AlertDto("crit", "Redis unreachable", "The Redis cache / leaderboard backplane is not responding", "now"));
            if (stuckPending > 0)
                alerts.Add(new AlertDto("warn", "Stuck settlements", $"{stuckPending} transaction(s) pending over 5 minutes", "now"));
            if (openReports > 0)
                alerts.Add(new AlertDto("warn", "Reports queue", $"{openReports} player report(s) awaiting review", "queue"));

            // Healthy baseline whenever nothing is critical, so the panel always shows a live signal.
            if (!alerts.Any(a => a.Severity == "crit"))
                alerts.Add(new AlertDto("ok", "Systems nominal", "Wallet ledger healthy — no failed transactions in 24h", "live"));

            return alerts;
        }
    }

    /// <summary>A single dashboard alert. <c>Severity</c> = crit | warn | ok.</summary>
    public sealed record AlertDto(string Severity, string Title, string Detail, string When);
}
