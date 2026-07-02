using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Services.Loyalty;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>The Loyalty store: <c>GET</c> the catalog + balance, <c>POST redeem</c> to spend LP. Redeem is
    /// idempotent on a client-supplied key, so a retry never double-spends.</summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class LoyaltyController : ControllerBase
    {
        private readonly ILoyaltyService _loyalty;

        public LoyaltyController(ILoyaltyService loyalty)
        {
            _loyalty = loyalty;
        }

        /// <summary>The caller's LP balance + the store catalog (each item annotated affordable / unlocked).</summary>
        [HttpGet]
        public async Task<IActionResult> Store()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            var dto = await _loyalty.GetStoreAsync(me.Value);
            return dto == null ? NotFound() : Ok(dto);
        }

        /// <summary>Redeem a store item. The client MUST send a stable <c>idempotencyKey</c> per redemption intent
        /// (generate once on the buy tap, reuse on retry) so a re-send can't double-spend.</summary>
        [HttpPost("redeem")]
        public async Task<IActionResult> Redeem([FromBody] RedeemRequest req)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (req == null || string.IsNullOrWhiteSpace(req.ItemId)) return BadRequest(new { error = "itemId is required." });
            if (string.IsNullOrWhiteSpace(req.IdempotencyKey)) return BadRequest(new { error = "idempotencyKey is required." });

            var res = await _loyalty.RedeemAsync(me.Value, req.ItemId, req.IdempotencyKey);
            return Ok(res);   // 200 always; res.Ok carries success/failure so the client always gets the result + message
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }

    /// <summary>Redeem body. <see cref="IdempotencyKey"/> is a client-generated stable id per redemption intent.</summary>
    public sealed class RedeemRequest
    {
        public string ItemId { get; set; }
        public string IdempotencyKey { get; set; }
    }
}
