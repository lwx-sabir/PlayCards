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
        private readonly IPassAdService _ads;

        public PassController(IPassService pass, IPassAdService ads)
        {
            _pass = pass; _ads = ads;
        }

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

        /// <summary>
        /// Ask for permission to watch ads for a missed day. Returns a single-use, server-signed token to hand the ad
        /// SDK as custom data — the network echoes it back in its callback, which is what ties the view to this
        /// player, this cycle and this day. Refused for any day ads can't unlock.
        /// </summary>
        [HttpPost("ad-intent")]
        public async Task<IActionResult> AdIntent([FromBody] AdIntentRequest request)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (request == null || request.Node <= 0) return Ok(new Khela.Common.Pass.PassAdIntentDto { Ok = false, Error = "Which day?" });
            return Ok(await _ads.CreateIntentAsync(me.Value, request.PassKey, request.Node));
        }

        public sealed class AdIntentRequest
        {
            public string PassKey { get; set; }
            public int Node { get; set; }
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
