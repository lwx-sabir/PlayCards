using Khela.Game.Services.Redis;
using StackExchange.Redis;

namespace Khela.Game.Services.Presence
{
    public interface IPresenceService
    {
        Task MarkOnlineAsync(Guid userId, string connectionId);
        Task MarkOfflineAsync(Guid userId, string connectionId);
        Task<bool> IsOnlineAsync(Guid userId);
        Task<HashSet<Guid>> FilterOnlineAsync(IReadOnlyCollection<Guid> userIds);
    }

    /// <summary>
    /// Online presence in Redis: a per-user connection set (multi-device safe) plus a global online set.
    /// A user is online while they hold at least one live SignalR connection. Tracked from the ChatHub
    /// connect/disconnect lifecycle. Stateless over Redis → registered as a singleton.
    /// </summary>
    public sealed class PresenceService : IPresenceService
    {
        private const string OnlineSet = "presence:online";
        private static readonly TimeSpan ConnTtl = TimeSpan.FromHours(12); // safety net vs missed disconnects

        private readonly IRedisService _redis;

        public PresenceService(IRedisService redis) => _redis = redis;

        private static string ConnKey(Guid userId) => $"presence:conns:{userId}";

        // Add the connection, refresh the safety TTL, and mark online — atomically, so a concurrent
        // disconnect cannot interleave between the add and the online-set write.
        private const string OnlineLua =
            "redis.call('SADD', KEYS[1], ARGV[1]) " +
            "redis.call('PEXPIRE', KEYS[1], ARGV[3]) " +
            "redis.call('SADD', KEYS[2], ARGV[2]) " +
            "return 1";

        // Remove the connection and, only if it was the user's last one, clear the online flag —
        // atomic remove-and-test closes the check-then-act race that could mark a live user offline.
        private const string OfflineLua =
            "redis.call('SREM', KEYS[1], ARGV[1]) " +
            "if redis.call('SCARD', KEYS[1]) == 0 then redis.call('SREM', KEYS[2], ARGV[2]) end " +
            "return 1";

        public async Task MarkOnlineAsync(Guid userId, string connectionId)
        {
            await _redis.GetDatabase().ScriptEvaluateAsync(OnlineLua,
                new RedisKey[] { ConnKey(userId), OnlineSet },
                new RedisValue[] { connectionId, userId.ToString(), (long)ConnTtl.TotalMilliseconds });
        }

        public async Task MarkOfflineAsync(Guid userId, string connectionId)
        {
            await _redis.GetDatabase().ScriptEvaluateAsync(OfflineLua,
                new RedisKey[] { ConnKey(userId), OnlineSet },
                new RedisValue[] { connectionId, userId.ToString() });
        }

        public Task<bool> IsOnlineAsync(Guid userId)
            => _redis.GetDatabase().SetContainsAsync(OnlineSet, userId.ToString());

        public async Task<HashSet<Guid>> FilterOnlineAsync(IReadOnlyCollection<Guid> userIds)
        {
            var online = new HashSet<Guid>();
            if (userIds.Count == 0) return online;

            var ids = userIds is Guid[] arr ? arr : userIds.ToArray();
            var members = Array.ConvertAll(ids, id => (RedisValue)id.ToString());
            // SMISMEMBER: one round-trip for the whole batch instead of N sequential membership checks.
            var present = await _redis.GetDatabase().SetContainsAsync(OnlineSet, members);
            for (int i = 0; i < ids.Length; i++)
                if (present[i]) online.Add(ids[i]);
            return online;
        }
    }
}
