using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Khela.Common.Reports;
using Khela.Game.Services.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Player/message reporting. User endpoints (<c>/api/reports/*</c>) create reports (rate-limited, deduped).
    /// Admin endpoints (<c>/api/admin/reports*</c>) list + resolve the queue and are DEV-GATED for now — before
    /// production they move behind a proper Admin role (same pre-prod TODO as ReconciliationController).
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ReportsController : ControllerBase
    {
        private readonly IReportsService _reports;
        private readonly IWebHostEnvironment _env;

        public ReportsController(IReportsService reports, IWebHostEnvironment env)
        {
            _reports = reports;
            _env = env;
        }

        /// <summary>Report a specific message (the offending content must be snapshotted in the request).</summary>
        [HttpPost("message")]
        public Task<IActionResult> ReportMessage([FromBody] ReportMessageRequest req)
            => Run(me => _reports.CreateMessageReportAsync(me, req));

        /// <summary>Report a player.</summary>
        [HttpPost("player")]
        public Task<IActionResult> ReportPlayer([FromBody] ReportPlayerRequest req)
            => Run(me => _reports.CreatePlayerReportAsync(me, req));

        // ---- Admin (dev-gated; TODO: gate behind an Admin role before prod) ----

        /// <summary>Admin: paged report queue, optionally filtered by status.</summary>
        [Authorize(Policy = "Admin")]
        [HttpGet("/api/admin/reports")]
        public async Task<IActionResult> AdminList([FromQuery] ReportStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        {
            return Ok(await _reports.ListAsync(status, page, pageSize));
        }

        /// <summary>Admin: resolve a report (set status + note).</summary>
        [Authorize(Policy = "Admin")]
        [HttpPost("/api/admin/reports/{id:guid}/resolve")]
        public async Task<IActionResult> AdminResolve(Guid id, [FromBody] ResolveReportRequest req)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            var (ok, error) = await _reports.ResolveAsync(id, me.Value, req);
            return ok ? Ok() : BadRequest(new { message = error });
        }

        private async Task<IActionResult> Run(Func<Guid, Task<(bool ok, string error)>> action)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            var (ok, error) = await action(me.Value);
            return ok ? Ok() : BadRequest(new { message = error });
        }

        private Guid? GetUserGuid()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : null;
        }
    }
}
