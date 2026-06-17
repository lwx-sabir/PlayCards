using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Khela.Game.Services.Friends;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class FriendsController : ControllerBase
    {
        private readonly IFriendsService _friends;

        public FriendsController(IFriendsService friends)
        {
            _friends = friends;
        }

        /// <summary>The caller's accepted friends (with online status).</summary>
        [HttpGet]
        public async Task<IActionResult> Friends()
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            return Ok(await _friends.GetFriendsAsync(me.Value));
        }

        /// <summary>Incoming pending friend requests.</summary>
        [HttpGet("pending")]
        public async Task<IActionResult> Pending()
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            return Ok(await _friends.GetPendingAsync(me.Value));
        }

        /// <summary>People the caller recently played with — the discovery list for users with no friends yet.</summary>
        [HttpGet("recent")]
        public async Task<IActionResult> Recent([FromQuery] int limit = 20)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            return Ok(await _friends.RecentPlayersAsync(me.Value, limit));
        }

        /// <summary>Search by display name or user id.</summary>
        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 20)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            return Ok(await _friends.SearchAsync(me.Value, q, limit));
        }

        [HttpPost("{otherUserId:guid}/request")]
        public async Task<IActionResult> Request(Guid otherUserId)
            => await Run(me => _friends.SendRequestAsync(me, otherUserId));

        [HttpPost("{requesterId:guid}/respond")]
        public async Task<IActionResult> Respond(Guid requesterId, [FromQuery] bool accept = true)
            => await Run(me => _friends.RespondAsync(me, requesterId, accept));

        [HttpDelete("{otherUserId:guid}")]
        public async Task<IActionResult> Remove(Guid otherUserId)
            => await Run(me => _friends.RemoveAsync(me, otherUserId));

        [HttpPost("{otherUserId:guid}/block")]
        public async Task<IActionResult> Block(Guid otherUserId)
            => await Run(me => _friends.BlockAsync(me, otherUserId));

        private async Task<IActionResult> Run(Func<Guid, Task<(bool ok, string error)>> action)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            var (ok, error) = await action(me.Value);
            return ok ? Ok() : BadRequest(new { message = error });
        }

        private Guid? GetUserGuid()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : null;
        }
    }
}
