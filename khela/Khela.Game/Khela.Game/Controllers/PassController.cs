using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Services.Pass;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// The Monthly Pass: a daily ladder with a free track and a Golden (real-money subscription) track.
    /// Everything here is server-authoritative — the client renders what GET returns and taps to claim.
    /// See docs/PASS_SPEC.md.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class PassController : ControllerBase
    {
        private readonly IPassService _pass;

        public PassController(IPassService pass) => _pass = pass;

        /// <summary>The whole pass screen: ladder, per-node state, what's claimable, golden state and countdowns.
        /// <c>Active = false</c> simply means no pass is running — the client hides it.</summary>
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string passKey = null)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _pass.GetStateAsync(me.Value, passKey));
        }

        /// <summary>Claim one node. Omit <c>node</c> for today's; an earlier one is a catch-up (free for subscribers,
        /// or paid with rewarded-ad credits when <c>useAds</c> is set).</summary>
        [HttpPost("claim")]
        public async Task<IActionResult> Claim([FromBody] ClaimRequest request)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            var res = await _pass.ClaimAsync(me.Value, request?.PassKey, request?.Node, request?.UseAds ?? false);
            return Ok(res);   // 200 always; res.Ok carries success/failure, like the rest of the reward endpoints
        }

        /// <summary>Claim everything currently free, oldest first. Never spends ad credits.</summary>
        [HttpPost("claim-all")]
        public async Task<IActionResult> ClaimAll([FromBody] ClaimRequest request = null)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _pass.ClaimAllAsync(me.Value, request?.PassKey));
        }

        public sealed class ClaimRequest
        {
            /// <summary>Which pass program; null ⇒ the monthly pass.</summary>
            public string PassKey { get; set; }
            /// <summary>Which day; null ⇒ today's node.</summary>
            public int? Node { get; set; }
            /// <summary>Spend rewarded-ad credits to unlock a missed day.</summary>
            public bool UseAds { get; set; }
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
