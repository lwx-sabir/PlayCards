using System;
using System.Linq;
using System.Threading.Tasks;
using Khela.Game.Database;
using Khela.Game.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Ops + debug endpoints for settlement reconciliation. DEV-GATED (404 outside Development); before
    /// production this must move behind a proper Admin role. The read endpoints never mutate money; POST run
    /// triggers one idempotent heal pass so you can debug the sweeper WITHOUT enabling the background loop.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ReconciliationController : ControllerBase
    {
        private readonly AppDbContext db;
        private readonly IWebHostEnvironment env;
        private readonly IConfiguration config;
        private readonly SettlementReconciliationService sweeper;

        public ReconciliationController(AppDbContext db, IWebHostEnvironment env, IConfiguration config,
            SettlementReconciliationService sweeper)
        {
            this.db = db;
            this.env = env;
            this.config = config;
            this.sweeper = sweeper;
        }

        /// <summary>Current sweeper toggle + config + the unresolved-problem counts. Read-only — handy for
        /// confirming whether the background sweeper is on while debugging.</summary>
        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            if (!env.IsDevelopment()) return NotFound();
            return Ok(new
            {
                enabled = config.GetValue("Reconciliation:Enabled", false),
                intervalSeconds = config.GetValue("Reconciliation:IntervalSeconds", 60),
                roundSettleTimeoutSeconds = config.GetValue("Reconciliation:RoundSettleTimeoutSeconds", 120),
                orphanRefundQuietSeconds = config.GetValue("Reconciliation:OrphanRefundQuietSeconds", 600),
                unresolvedSettleFailed = await db.GameHandParticipants.CountAsync(p => !p.Resolved && p.Outcome == "settle_failed"),
                unresolvedSettleMismatch = await db.GameHandParticipants.CountAsync(p => !p.Resolved && p.Outcome == "settle_mismatch"),
            });
        }

        /// <summary>Lists unresolved settlement problems for ops: stranded <c>settle_failed</c> seats (the
        /// sweeper heals these) and <c>settle_mismatch</c> tripwire rows (already paid; flagged for review).</summary>
        [HttpGet("unresolved")]
        public async Task<IActionResult> Unresolved()
        {
            if (!env.IsDevelopment()) return NotFound(); // TODO: gate behind an Admin role before prod

            var items = await db.GameHandParticipants
                .Where(p => !p.Resolved && (p.Outcome == "settle_failed" || p.Outcome == "settle_mismatch"))
                .OrderBy(p => p.HandId)
                .Take(500)
                .Select(p => new
                {
                    p.ParticipantId,
                    p.HandId,
                    p.UserId,
                    p.SeatNumber,
                    p.HandIndex,
                    p.Outcome,
                    Owed = p.Payout,
                    p.WalletDebitTxId,
                    p.WalletCreditTxId,
                    p.MetadataJson
                })
                .ToListAsync();

            return Ok(new
            {
                count = items.Count,
                settleFailed = items.Count(i => i.Outcome == "settle_failed"),
                settleMismatch = items.Count(i => i.Outcome == "settle_mismatch"),
                items
            });
        }

        /// <summary>
        /// DEBUG: run ONE idempotent reconciliation pass right now and return the summary — regardless of the
        /// <c>Reconciliation:Enabled</c> flag, so you can exercise the sweeper without turning the background
        /// loop on. It mutates the ledger (idempotently), hence dev-gated.
        /// </summary>
        [HttpPost("run")]
        public async Task<IActionResult> Run()
        {
            if (!env.IsDevelopment()) return NotFound();
            var summary = await sweeper.RunPassAsync(HttpContext.RequestAborted);
            return Ok(summary);
        }

        /// <summary>
        /// OPS: mark a <c>settle_mismatch</c> row reviewed (Resolved=true) after a human checks the engine
        /// drift it flagged — clears it from the unresolved queue. Does NOT move money. <c>settle_failed</c>
        /// rows are healed by the sweeper, not here.
        /// </summary>
        [HttpPost("resolve/{participantId}")]
        public async Task<IActionResult> Resolve(Guid participantId)
        {
            if (!env.IsDevelopment()) return NotFound();
            var row = await db.GameHandParticipants.FirstOrDefaultAsync(p => p.ParticipantId == participantId);
            if (row == null) return NotFound(new { message = "participant not found" });
            if (row.Outcome != "settle_mismatch")
                return BadRequest(new { message = "only settle_mismatch rows may be manually resolved; settle_failed rows are healed by the sweeper." });
            row.Resolved = true;
            await db.SaveChangesAsync();
            return Ok(new { row.ParticipantId, row.Resolved });
        }
    }
}
