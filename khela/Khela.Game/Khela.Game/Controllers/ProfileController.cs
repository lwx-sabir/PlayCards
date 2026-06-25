using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Khela.Common.Profiles;
using Khela.Game.Services.Profile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Read/edit of the game profile. <c>GET me</c> is the caller's full profile; <c>GET {userId}</c> is another
    /// player's PUBLIC profile (block-aware — 404 if blocked in either direction); <c>PATCH me</c> edits the
    /// editable fields (name/cosmetics/bio/status), moderated server-side.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ProfileController : ControllerBase
    {
        private readonly IProfileService _profiles;

        public ProfileController(IProfileService profiles) => _profiles = profiles;

        /// <summary>The caller's own profile (+ stats, public linked socials).</summary>
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            var dto = await _profiles.GetMyProfileAsync(me.Value);
            return dto == null ? NotFound() : Ok(dto);
        }

        /// <summary>Another player's public profile. 404 if not found or blocked.</summary>
        [HttpGet("{userId:guid}")]
        public async Task<IActionResult> Public(Guid userId)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            var dto = await _profiles.GetPublicProfileAsync(me.Value, userId);
            return dto == null ? NotFound() : Ok(dto);
        }

        /// <summary>Edit the caller's profile (DisplayName/Avatar/Frame/Flag/Bio/StatusMessage).</summary>
        [HttpPatch("me")]
        public async Task<IActionResult> Update([FromBody] UpdateProfileRequest req)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            var (ok, error) = await _profiles.UpdateAsync(me.Value, req);
            return ok ? Ok() : BadRequest(new { message = error });
        }

        private Guid? GetUserGuid()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : null;
        }
    }
}
