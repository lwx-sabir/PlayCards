using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Khela.Common.Avatar;
using Khela.Game.Database;
using Khela.Game.Services.Avatar;
using Khela.Game.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// The player's 3D avatar config (BoZo), server-synced + sanitized. The server is the source of truth so any client
    /// can render this player's avatar at their seat. GET your own or anyone's; PUT saves your own after the server
    /// clamps it to human bounds (a hacked client can't persist an inhuman avatar).
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public sealed class AvatarController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly Services.Cosmetics.ICosmeticsService _cosmetics;

        public AvatarController(AppDbContext db, Services.Cosmetics.ICosmeticsService cosmetics)
        {
            _db = db;
            _cosmetics = cosmetics;
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>The caller's avatar (null if not set yet).</summary>
        [HttpGet("me")]
        public async Task<IActionResult> Mine()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(new { avatar = Parse(await ConfigJsonAsync(me.Value)) });
        }

        /// <summary>Any player's avatar — used to render other seated players.</summary>
        [HttpGet("{userId:guid}")]
        public async Task<IActionResult> Get(Guid userId)
            => Ok(new { avatar = Parse(await ConfigJsonAsync(userId)) });

        /// <summary>Save the caller's avatar. The server SANITIZES it (clamps every value to human bounds) before storing.</summary>
        [HttpPut("me")]
        public async Task<IActionResult> Save([FromBody] AvatarDto avatar)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (avatar == null) return BadRequest(new { message = "Missing avatar." });

            var clean = AvatarSanitizer.Sanitize(avatar);

            // Entitlement gate: every cataloged outfit must be owned and legally coloured (Fixed = designed colours,
            // Palette = the SKU's grid). A hacked client can't persist gear it didn't buy. Normalizes colours in place.
            var entitled = await _cosmetics.ValidateEquipsAsync(me.Value, clean);
            if (!entitled.Ok) return BadRequest(new { message = entitled.Error });

            // Self-heal a missing profile rather than refusing the save. The profile is normally created during
            // registration, but that bootstrap deliberately swallows its own failures so it can never block a login —
            // which means a single hiccup there leaves an account with no profile row, and from then on EVERY avatar
            // save 404s and the player can never finish onboarding. There is nothing to look up here that we can't
            // simply create, so create it.
            var prof = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == me.Value);
            if (prof == null)
            {
                prof = await CreateMissingProfileAsync(me.Value);
                if (prof == null) return StatusCode(500, new { message = "Could not create a profile for this account." });
            }

            prof.AvatarConfig = JsonSerializer.Serialize(clean, Json);
            prof.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { avatar = clean });   // echo the sanitized result so the client re-syncs to what was stored
        }

        /// <summary>
        /// Create the profile row this account should already have had. Mirrors the fields registration seeds, and
        /// tolerates losing a race with another request creating the same row (unique on UserId) by re-reading.
        /// </summary>
        private async Task<Database.Models.UserProfile> CreateMissingProfileAsync(Guid userId)
        {
            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.ToString());
            var region = (user?.CountryCode ?? "").Trim().ToUpperInvariant();
            if (region.Length != 2) region = "ZZ";

            // A safe, non-colliding placeholder — the player renames themselves in onboarding anyway.
            var name = string.IsNullOrWhiteSpace(user?.UserName)
                ? $"Player{userId.ToString("N").Substring(0, 6)}"
                : user.UserName.Trim();
            if (name.Length > 24) name = name.Substring(0, 24);

            try
            {
                var prof = new Database.Models.UserProfile
                {
                    UserId = userId,
                    PublicId = await PublicPlayerId.AllocateAsync(_db),   // permanent player id, unique
                    DisplayName = name,
                    DisplayNameNormalized = name.ToUpperInvariant(),
                    Region = region
                };
                _db.UserProfiles.Add(prof);
                await _db.SaveChangesAsync();
                return prof;
            }
            catch (DbUpdateException)
            {
                // Someone else created it between our read and our write — take theirs.
                _db.ChangeTracker.Clear();
                return await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
            }
        }

        private async Task<string> ConfigJsonAsync(Guid userId) =>
            await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId)
                .Select(p => p.AvatarConfig).FirstOrDefaultAsync();

        private static AvatarDto Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonSerializer.Deserialize<AvatarDto>(json, Json); } catch { return null; }
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
