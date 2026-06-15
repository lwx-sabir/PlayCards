using System.Text.Json;
using Khela.Common.Social;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Services.Chat
{
    public sealed record ChatMessageDto(
        string Id, string SenderId, string SenderName, string RecipientId,
        int ChannelType, string ChannelKey, string Body, DateTime SentAt, int Moderation);

    public readonly record struct ChatSendResult(bool Ok, string Error, ChatMessageDto Message);

    public interface IChatService
    {
        Task<ChatSendResult> SendDmAsync(Guid senderId, Guid recipientId, string body);
        Task<ChatSendResult> SendChannelAsync(Guid senderId, ChatChannelType type, string channelKey, string body);
        Task<IReadOnlyList<ChatMessageDto>> GetDmHistoryAsync(Guid userId, Guid otherUserId, int count, DateTime? before);
        Task<IReadOnlyList<ChatMessageDto>> GetChannelRecentAsync(string channelKey, int count);
        Task<int> GetUnreadCountAsync(Guid userId);
        Task MarkDmReadAsync(Guid userId, Guid otherUserId);
    }

    /// <summary>
    /// Chat persistence + delivery prep. DMs are durable in MySQL (ChatMessage); Table/Global chat is
    /// ephemeral in Redis (recent-N ring + TTL) so high-volume room chat never hammers the DB. Every
    /// message passes IChatModerator before broadcast, and per-user rate limits run in Redis. The ChatHub
    /// does the SignalR fan-out; this service produces the moderated, persisted DTO.
    /// </summary>
    public sealed class ChatService : IChatService
    {
        private const int ChannelRingSize = 100;                       // recent messages kept per Redis channel
        private static readonly TimeSpan ChannelTtl = TimeSpan.FromHours(6);
        private const int RateLimitMax = 10;                           // messages per window
        private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(5);

        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly AppDbContext _db;
        private readonly IRedisService _redis;
        private readonly IChatModerator _moderator;

        public ChatService(AppDbContext db, IRedisService redis, IChatModerator moderator)
        {
            _db = db;
            _redis = redis;
            _moderator = moderator;
        }

        public async Task<ChatSendResult> SendDmAsync(Guid senderId, Guid recipientId, string body)
        {
            if (senderId == recipientId) return Fail("Cannot message yourself.");
            if (!await CheckRateLimitAsync(senderId)) return Fail("You're sending messages too fast.");

            var mod = await _moderator.ModerateAsync(body);
            if (mod.Outcome == ModerationOutcome.Rejected) return Fail("Message rejected.");

            var status = mod.Outcome == ModerationOutcome.Masked ? MessageModerationStatus.Flagged : MessageModerationStatus.Approved;
            var msg = new ChatMessage
            {
                SenderId = senderId,
                RecipientId = recipientId,
                Body = mod.Text,
                Moderation = status
            };
            _db.ChatMessages.Add(msg);
            await _db.SaveChangesAsync();

            var name = await DisplayNameAsync(senderId);
            return Ok(new ChatMessageDto(msg.MessageId.ToString(), senderId.ToString(), name, recipientId.ToString(),
                (int)ChatChannelType.Dm, "", msg.Body, msg.SentAt, (int)status));
        }

        public async Task<ChatSendResult> SendChannelAsync(Guid senderId, ChatChannelType type, string channelKey, string body)
        {
            if (type == ChatChannelType.Dm) return Fail("Use SendDm for direct messages.");
            if (string.IsNullOrWhiteSpace(channelKey)) return Fail("Missing channel.");
            if (!await CheckRateLimitAsync(senderId)) return Fail("You're sending messages too fast.");

            var mod = await _moderator.ModerateAsync(body);
            if (mod.Outcome == ModerationOutcome.Rejected) return Fail("Message rejected.");

            var status = mod.Outcome == ModerationOutcome.Masked ? MessageModerationStatus.Flagged : MessageModerationStatus.Approved;
            var name = await DisplayNameAsync(senderId);
            var dto = new ChatMessageDto(Guid.NewGuid().ToString(), senderId.ToString(), name, "",
                (int)type, channelKey, mod.Text, DateTime.UtcNow, (int)status);

            // Ephemeral room chat: keep only the recent ring in Redis (no MySQL write).
            var rdb = _redis.GetDatabase();
            var key = ChannelRedisKey(channelKey);
            await rdb.ListRightPushAsync(key, JsonSerializer.Serialize(dto, Json));
            await rdb.ListTrimAsync(key, -ChannelRingSize, -1);
            await rdb.KeyExpireAsync(key, ChannelTtl);

            return Ok(dto);
        }

        public async Task<IReadOnlyList<ChatMessageDto>> GetDmHistoryAsync(Guid userId, Guid otherUserId, int count, DateTime? before)
        {
            count = Math.Clamp(count, 1, 100);
            var q = _db.ChatMessages.AsNoTracking()
                .Where(m => (m.SenderId == userId && m.RecipientId == otherUserId)
                         || (m.SenderId == otherUserId && m.RecipientId == userId));
            if (before.HasValue) q = q.Where(m => m.SentAt < before.Value);

            var rows = await q.OrderByDescending(m => m.SentAt).Take(count).ToListAsync();

            var names = new Dictionary<Guid, string>();
            var result = new List<ChatMessageDto>(rows.Count);
            foreach (var m in rows)
            {
                if (!names.TryGetValue(m.SenderId, out var nm)) { nm = await DisplayNameAsync(m.SenderId); names[m.SenderId] = nm; }
                result.Add(new ChatMessageDto(m.MessageId.ToString(), m.SenderId.ToString(), nm, m.RecipientId.ToString(),
                    (int)ChatChannelType.Dm, "", m.Body, m.SentAt, (int)m.Moderation));
            }
            result.Reverse(); // chronological (oldest first)
            return result;
        }

        public async Task<IReadOnlyList<ChatMessageDto>> GetChannelRecentAsync(string channelKey, int count)
        {
            count = Math.Clamp(count, 1, ChannelRingSize);
            var rdb = _redis.GetDatabase();
            var vals = await rdb.ListRangeAsync(ChannelRedisKey(channelKey), -count, -1);

            var list = new List<ChatMessageDto>(vals.Length);
            foreach (var v in vals)
            {
                if (v.IsNullOrEmpty) continue;
                try
                {
                    var dto = JsonSerializer.Deserialize<ChatMessageDto>(v!, Json);
                    if (dto != null) list.Add(dto);
                }
                catch { /* skip a malformed entry */ }
            }
            return list;
        }

        public Task<int> GetUnreadCountAsync(Guid userId)
            => _db.ChatMessages.AsNoTracking().CountAsync(m => m.RecipientId == userId && m.ReadAt == null);

        public async Task MarkDmReadAsync(Guid userId, Guid otherUserId)
        {
            var now = DateTime.UtcNow;
            var unread = await _db.ChatMessages
                .Where(m => m.RecipientId == userId && m.SenderId == otherUserId && m.ReadAt == null)
                .ToListAsync();
            foreach (var m in unread) m.ReadAt = now;
            if (unread.Count > 0) await _db.SaveChangesAsync();
        }

        // ---- helpers ----
        private async Task<bool> CheckRateLimitAsync(Guid userId)
        {
            var rdb = _redis.GetDatabase();
            var key = $"chat:rl:{userId}";
            var n = await rdb.StringIncrementAsync(key);
            if (n == 1) await rdb.KeyExpireAsync(key, RateLimitWindow);
            return n <= RateLimitMax;
        }

        private async Task<string> DisplayNameAsync(Guid userId)
            => (await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId)
                    .Select(p => p.DisplayName).FirstOrDefaultAsync()) ?? "Player";

        private static string ChannelRedisKey(string channelKey) => $"chat:ch:{channelKey}";

        private static ChatSendResult Ok(ChatMessageDto dto) => new(true, null, dto);
        private static ChatSendResult Fail(string error) => new(false, error, default);
    }
}
