using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Khela.Game.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Non-real-time chat: DM history, unread count, mark-read, and a room's recent buffer. Live sending
    /// + delivery happens on the ChatHub (SignalR); these endpoints are for loading history and badges.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ChatController : ControllerBase
    {
        private readonly IChatService _chat;

        public ChatController(IChatService chat)
        {
            _chat = chat;
        }

        /// <summary>A DM conversation's history (chronological), paged backwards via ?before=.</summary>
        [HttpGet("dm/{otherUserId:guid}")]
        public async Task<IActionResult> DmHistory(Guid otherUserId, [FromQuery] int count = 50, [FromQuery] DateTime? before = null)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            return Ok(await _chat.GetDmHistoryAsync(me.Value, otherUserId, count, before));
        }

        /// <summary>Mark every unread DM from a given sender as read.</summary>
        [HttpPost("dm/{otherUserId:guid}/read")]
        public async Task<IActionResult> MarkRead(Guid otherUserId)
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            await _chat.MarkDmReadAsync(me.Value, otherUserId);
            return Ok();
        }

        /// <summary>Total unread DMs for the caller (the chat badge).</summary>
        [HttpGet("unread")]
        public async Task<IActionResult> Unread()
        {
            var me = GetUserGuid();
            if (me == null) return Unauthorized();
            return Ok(new { count = await _chat.GetUnreadCountAsync(me.Value) });
        }

        /// <summary>Recent messages of a Table/Global room (Redis-backed ephemeral buffer).</summary>
        [HttpGet("channel/{channelKey}")]
        public async Task<IActionResult> Channel(string channelKey, [FromQuery] int count = 50)
            => Ok(await _chat.GetChannelRecentAsync(channelKey, count));

        private Guid? GetUserGuid()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : null;
        }
    }
}
