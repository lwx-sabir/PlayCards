using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Services.Rewards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// The player's claimable-reward inbox. Rewards (level-up, daily, pass, achievement…) are enqueued server-side and
    /// COLLECTED here by tapping — the wallet is credited only on claim (idempotent), never auto.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class RewardsController : ControllerBase
    {
        private readonly IRewardService _rewards;

        public RewardsController(IRewardService rewards) => _rewards = rewards;

        /// <summary>The caller's pending (unclaimed, unexpired) rewards.</summary>
        [HttpGet]
        public async Task<IActionResult> Pending()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _rewards.GetPendingAsync(me.Value));
        }

        /// <summary>Claim ONE reward by id. Idempotent — a re-tap never double-pays.</summary>
        [HttpPost("{id}/claim")]
        public async Task<IActionResult> Claim(Guid id)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            var res = await _rewards.ClaimAsync(me.Value, id);
            return Ok(res);   // 200 always; res.Ok carries success/failure
        }

        /// <summary>Claim ALL pending rewards.</summary>
        [HttpPost("claim-all")]
        public async Task<IActionResult> ClaimAll()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _rewards.ClaimAllAsync(me.Value));
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
