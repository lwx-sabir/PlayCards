using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Services.Missions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Daily missions: list the player's set (lazily assigned per UTC day), claim a completed mission (reward straight
    /// to balance), and claim the complete-all bundle. Progress is advanced server-side at settle — never by the client.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class MissionsController : ControllerBase
    {
        private readonly IMissionService _missions;

        public MissionsController(IMissionService missions) => _missions = missions;

        /// <summary>The caller's daily missions + bundle state + reset time.</summary>
        [HttpGet("daily")]
        public async Task<IActionResult> Daily()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _missions.GetDailyAsync(me.Value));
        }

        /// <summary>Claim a completed mission by its instance id. Idempotent.</summary>
        [HttpPost("{id}/claim")]
        public async Task<IActionResult> Claim(Guid id)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _missions.ClaimAsync(me.Value, id));   // 200 always; res.Ok carries success/failure
        }

        /// <summary>Claim the "complete all daily missions" bundle (once per UTC day).</summary>
        [HttpPost("bundle/claim")]
        public async Task<IActionResult> ClaimBundle()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _missions.ClaimBundleAsync(me.Value));
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
