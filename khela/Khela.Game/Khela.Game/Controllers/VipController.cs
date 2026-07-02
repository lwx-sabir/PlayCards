using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Database;
using Khela.Game.Services.Vip;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Read-only VIP status for the profile / VIP screen (<c>GET me</c>) + the "hide my badge" opt-out. Status-Point
    /// accrual + tier changes happen server-side (VipService at settle / on the monthly review), never here.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class VipController : ControllerBase
    {
        private readonly IVipService _vip;
        private readonly AppDbContext _db;

        public VipController(IVipService vip, AppDbContext db)
        {
            _vip = vip;
            _db = db;
        }

        /// <summary>The caller's live VIP status (tier, badge, trailing SP, benefit multiplier, next-tier progress).</summary>
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            var dto = await _vip.GetMyVipStatusAsync(me.Value);
            return dto == null ? NotFound() : Ok(dto);
        }

        /// <summary>Toggle the "hide my VIP badge from others" opt-out.</summary>
        [HttpPost("me/hide-badge")]
        public async Task<IActionResult> SetHideBadge([FromQuery] bool hidden)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            var n = await _db.UserProfiles.Where(p => p.UserId == me.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.HideVipBadge, hidden));
            return n == 0 ? NotFound() : Ok(new { hideVipBadge = hidden });
        }

        /// <summary>Spend Loyalty Points to keep your current VIP level (the live, non-IAP keep-up).</summary>
        [HttpPost("maintain")]
        public async Task<IActionResult> Maintain()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            var res = await _vip.MaintainWithLpAsync(me.Value);
            return Ok(res);   // 200 always; res.Ok carries success/failure
        }

        /// <summary>Admin/test seam for the "VIP Booster" IAP items (the real IAP receipt flow calls
        /// IVipService.ApplyVipBoosterAsync directly). kind = "time" (extend) or "level" (+1).</summary>
        [HttpPost("admin/booster")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> ApplyBooster([FromQuery] string userId, [FromQuery] string kind, [FromQuery] string idemKey)
        {
            if (!Guid.TryParse(userId, out var uid)) return BadRequest(new { error = "valid userId required" });
            if (string.IsNullOrWhiteSpace(idemKey)) return BadRequest(new { error = "idemKey required" });
            var bk = (kind != null && kind.StartsWith("level", StringComparison.OrdinalIgnoreCase))
                ? VipBoosterKind.LevelUp : VipBoosterKind.Time;
            var ok = await _vip.ApplyVipBoosterAsync(uid, bk, idemKey);
            return Ok(new { ok, kind = bk.ToString() });
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
