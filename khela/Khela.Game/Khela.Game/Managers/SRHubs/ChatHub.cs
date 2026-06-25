using Khela.Common.Social;
using Khela.Game.Services.Chat;
using Khela.Game.Services.Presence;
using Khela.Game.Services.Profile;
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
        private readonly IPresenceService _presence;
        private readonly IProfileService _profiles;

        public ChatHub(IChatService chat, IPresenceService presence, IProfileService profiles)
        {
            _chat = chat;
            _presence = presence;
            _profiles = profiles;
        }

        public override async Task OnConnectedAsync()
        {
            if (TryUser(out var uid)) await _presence.MarkOnlineAsync(uid, Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            if (TryUser(out var uid))
            {
                await _presence.MarkOfflineAsync(uid, Context.ConnectionId);
                // Only the user's LAST connection dropping flips them offline — stamp LastSeenAt at that point.
                if (!await _presence.IsOnlineAsync(uid)) await _profiles.SetLastSeenAsync(uid);
            }
            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendDm(string recipientId, string body)
        {
            if (!TryUser(out var senderId) || !Guid.TryParse(recipientId, out var rid)) return;

            var res = await _chat.SendDmAsync(senderId, rid, body);
            if (!res.Ok) { await Clients.Caller.SendAsync("ChatError", res.Error); return; }

            await Clients.User(recipientId).SendAsync("ChatMessage", res.Message);
            await Clients.Caller.SendAsync("ChatMessage", res.Message);
        }

        public async Task JoinChannel(string channelKey)
        {
            if (!TryUser(out var uid) || !await _chat.CanAccessChannelAsync(uid, channelKey))
            { await Clients.Caller.SendAsync("ChatError", "You can't join this channel."); return; }
            await Groups.AddToGroupAsync(Context.ConnectionId, channelKey);
        }

        public Task LeaveChannel(string channelKey) => Groups.RemoveFromGroupAsync(Context.ConnectionId, channelKey);

        public async Task SendChannel(int channelType, string channelKey, string body)
        {
            if (!TryUser(out var senderId)) return;
            if (!await _chat.CanAccessChannelAsync(senderId, channelKey))
            { await Clients.Caller.SendAsync("ChatError", "You can't post in this channel."); return; }

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
