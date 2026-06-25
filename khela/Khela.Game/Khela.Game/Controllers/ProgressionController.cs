using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Services.Progression;
using Khela.Game.Services.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Read-only progression for the profile XP bar (<c>GET me</c>), plus an admin knob to retune the daily
    /// XP cap at runtime. XP accrual itself happens server-side at settle (ProgressionService), never here.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ProgressionController : ControllerBase
    {
        private readonly IProgressionService _progression;
        private readonly IRedisService _redis;

        public ProgressionController(IProgressionService progression, IRedisService redis)
        {
            _progression = progression;
            _redis = redis;
        }

        /// <summary>The caller's live level / into-level XP / XpToNext / daily-XP-remaining for the bar.</summary>
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            var dto = await _progression.GetMyProgressionAsync(me.Value);
            return dto == null ? NotFound() : Ok(dto);
        }

        /// <summary>
        /// Admin: set the daily XP cap at runtime (no redeploy). Persisted in Redis; a negative value clears
        /// the override so the configured default applies again.
        /// </summary>
        [HttpPost("admin/daily-cap")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> SetDailyCap([FromQuery] long value)
        {
            var db = _redis.GetDatabase();
            if (value < 0) await db.KeyDeleteAsync("progression:dailyXpCap");
            else await db.StringSetAsync("progression:dailyXpCap", value);
            return Ok(new { dailyXpCap = value < 0 ? (long?)null : value });
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
