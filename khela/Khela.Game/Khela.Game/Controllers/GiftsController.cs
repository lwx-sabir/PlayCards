using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Khela.Game.Services.Gifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class GiftsController : ControllerBase
    {
        private readonly IGiftService _gifts;

        public GiftsController(IGiftService gifts)
        {
            _gifts = gifts;
        }

        /// <summary>Unclaimed gifts for the caller.</summary>
        [HttpGet("pending")]
        public async Task<IActionResult> Pending()
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            return Ok(await _gifts.GetPendingAsync(me.Value));
        }

        /// <summary>How many free gifts the caller can still send today.</summary>
        [HttpGet("remaining")]
        public async Task<IActionResult> Remaining()
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            return Ok(new { remaining = await _gifts.RemainingTodayAsync(me.Value) });
        }

        /// <summary>Send a free-chips gift to a player (daily-capped).</summary>
        [HttpPost("{recipientId:guid}")]
        public async Task<IActionResult> Send(Guid recipientId)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            var (ok, error) = await _gifts.SendAsync(me.Value, recipientId);
            return ok ? Ok() : BadRequest(new { message = error });
        }

        /// <summary>Claim all pending gifts → credits the wallet (idempotent).</summary>
        [HttpPost("claim")]
        public async Task<IActionResult> ClaimAll()
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            var (ok, error, claimed) = await _gifts.ClaimAllAsync(me.Value);
            return ok ? Ok(new { claimed }) : BadRequest(new { message = error });
        }

        private Guid? GetUserGuid()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : null;
        }
    }
}
