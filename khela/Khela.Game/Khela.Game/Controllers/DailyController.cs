using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Common.Daily;
using Khela.Game.Services.Daily;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// The daily login reward: a fixed-length ladder that starts the day a player first sees it and repeats, with one
    /// free day per calendar day and missed days buyable with verified rewarded ads.
    ///
    /// Server-authoritative throughout — the client renders what GET returns and taps to claim. Which day the player
    /// is on is decided here from their own timezone, so a changed device clock reaches nothing.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DailyController : ControllerBase
    {
        private readonly IDailyService _daily;
        private readonly IDailyAdService _ads;

        public DailyController(IDailyService daily, IDailyAdService ads)
        {
            _daily = daily; _ads = ads;
        }

        /// <summary>The whole daily screen: the ladder, per-day state, what's claimable and the countdowns.
        /// <c>Active = false</c> simply means no daily reward is configured — the client hides it.</summary>
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _daily.GetStateAsync(me.Value));
        }

        /// <summary>Claim one day. Omit <c>node</c> for today's; an earlier one is a catch-up, which needs
        /// rewarded-ad credits unless the bypass switch is on.</summary>
        [HttpPost("claim")]
        public async Task<IActionResult> Claim([FromBody] DailyClaimRequest request = null)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            var res = await _daily.ClaimAsync(me.Value, request?.Node, request?.UseAds ?? false);
            return Ok(res);   // 200 always; res.Ok carries success/failure, like the other reward endpoints
        }

        /// <summary>
        /// Ask for permission to watch ads for a missed day. Returns a single-use, server-signed token to hand the ad
        /// SDK as custom data — the network echoes it back in its callback, which is what ties the view to this
        /// player, this run and this day. Refused for any day ads can't unlock.
        /// </summary>
        [HttpPost("ad-intent")]
        public async Task<IActionResult> AdIntent([FromBody] DailyAdIntentRequest request)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (request == null || request.Node <= 0) return Ok(new DailyAdIntentDto { Ok = false, Error = "Which day?" });
            return Ok(await _ads.CreateIntentAsync(me.Value, request.Node));
        }

        public sealed class DailyAdIntentRequest
        {
            public int Node { get; set; }
        }

        public sealed class DailyClaimRequest
        {
            /// <summary>Which day; null ⇒ the first one currently claimable.</summary>
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
