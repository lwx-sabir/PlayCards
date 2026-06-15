using Khela.Common.Social;
using Khela.Game.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace Khela.Game.Managers.SRHubs
{
    /// <summary>
    /// Real-time chat. DMs reach the recipient's connections via Clients.User (works across server
    /// instances thanks to the Redis backplane); Table/Global rooms use SignalR groups. Every send runs
    /// through ChatService (moderation + rate-limit + persistence/Redis). Group keys come from the client
    /// (e.g. "table:{id}" / "global"); join the matching group to receive that room's messages.
    /// Client receives "ChatMessage" (a ChatMessageDto) and "ChatError" (a string).
    /// </summary>
    [Authorize]
    public sealed class ChatHub : Hub
    {
        private readonly IChatService _chat;

        public ChatHub(IChatService chat) => _chat = chat;

        public async Task SendDm(string recipientId, string body)
        {
            if (!TryUser(out var senderId) || !Guid.TryParse(recipientId, out var rid)) return;

            var res = await _chat.SendDmAsync(senderId, rid, body);
            if (!res.Ok) { await Clients.Caller.SendAsync("ChatError", res.Error); return; }

            await Clients.User(recipientId).SendAsync("ChatMessage", res.Message);
            await Clients.Caller.SendAsync("ChatMessage", res.Message);
        }

        public Task JoinChannel(string channelKey) => Groups.AddToGroupAsync(Context.ConnectionId, channelKey);

        public Task LeaveChannel(string channelKey) => Groups.RemoveFromGroupAsync(Context.ConnectionId, channelKey);

        public async Task SendChannel(int channelType, string channelKey, string body)
        {
            if (!TryUser(out var senderId)) return;

            var res = await _chat.SendChannelAsync(senderId, (ChatChannelType)channelType, channelKey, body);
            if (!res.Ok) { await Clients.Caller.SendAsync("ChatError", res.Error); return; }

            await Clients.Group(channelKey).SendAsync("ChatMessage", res.Message);
        }

        private bool TryUser(out Guid id)
        {
            var s = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(s, out id);
        }
    }
}
