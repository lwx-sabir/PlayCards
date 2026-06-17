using Khela.Common.Social;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Khela.Game.Services.Gifts
{
    public sealed record GiftDto(string Id, string SenderId, string SenderName, decimal Amount, int Currency, int Status, DateTime SentAt);

    public interface IGiftService
    {
        Task<(bool ok, string error)> SendAsync(Guid senderId, Guid recipientId);
        Task<(bool ok, string error, int claimed)> ClaimAllAsync(Guid userId);
        Task<IReadOnlyList<GiftDto>> GetPendingAsync(Guid userId);
        Task<int> RemainingTodayAsync(Guid userId);
    }

    /// <summary>
    /// The daily free-chips gift loop. Sending costs the sender nothing (free chips — NOT a wallet
    /// transfer); a per-day send cap is enforced in Redis. The recipient claims, which credits their
    /// wallet via WalletService, idempotent on the gift's CorrelationId so a retried claim never
    /// double-credits. Chips only — the token can never be gifted.
    /// </summary>
    public sealed class GiftService : IGiftService
    {
        private const decimal FreeGiftAmount = 1000m;
        private const int DailyGiftLimit = 20;
        private static readonly TimeSpan GiftExpiry = TimeSpan.FromDays(7);

        // INCR + first-write TTL + atomic cap check: rejects (and rolls back) the over-limit increment in
        // one round-trip, so the daily counter can never be left without a TTL or briefly exceed the cap.
        private const string CounterLua =
            "local v = redis.call('INCR', KEYS[1]) " +
            "if v == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end " +
            "if v > tonumber(ARGV[2]) then redis.call('DECR', KEYS[1]) return -1 end " +
            "return v";

        private readonly AppDbContext _db;
        private readonly IRedisService _redis;
        private readonly IWalletService _wallet;
        private readonly ILogger<GiftService> _logger;

        public GiftService(AppDbContext db, IRedisService redis, IWalletService wallet, ILogger<GiftService> logger)
        {
            _db = db;
            _redis = redis;
            _wallet = wallet;
            _logger = logger;
        }

        public async Task<(bool, string)> SendAsync(Guid senderId, Guid recipientId)
        {
            if (senderId == recipientId) return (false, "You can't gift yourself.");

            // Daily send cap in Redis (key carries the UTC date; TTL cleans it up). The increment, the
            // first-write TTL, and the cap check are one atomic script — no orphaned TTL, no brief overshoot.
            var rdb = _redis.GetDatabase();
            var key = DailyKey(senderId);
            var n = (long)await rdb.ScriptEvaluateAsync(CounterLua,
                new RedisKey[] { key },
                new RedisValue[] { (long)TimeSpan.FromDays(2).TotalMilliseconds, DailyGiftLimit });
            if (n < 0) return (false, $"Daily gift limit reached ({DailyGiftLimit}/day).");

            var giftId = Guid.NewGuid();
            _db.Gifts.Add(new Gift
            {
                GiftId = giftId,
                SenderId = senderId,
                RecipientId = recipientId,
                Currency = CurrencyType.Chips,   // free chips only — never the token
                Amount = FreeGiftAmount,
                Status = GiftStatus.Sent,
                SentAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(GiftExpiry),
                CorrelationId = $"gift:{giftId}"
            });
            try
            {
                await _db.SaveChangesAsync();
            }
            catch
            {
                await rdb.StringDecrementAsync(key); // release the slot we reserved if the insert failed
                throw;
            }
            return (true, null);
        }

        public async Task<(bool, string, int)> ClaimAllAsync(Guid userId)
        {
            var rdb = _redis.GetDatabase();
            var claimLock = $"gift:claiming:{userId}";
            // Single-flight per user: a concurrent double-tap on "claim" can't double-process or over-count.
            if (!await rdb.StringSetAsync(claimLock, "1", TimeSpan.FromSeconds(30), When.NotExists))
                return (true, null, 0);

            try
            {
                var now = DateTime.UtcNow;
                var pending = await _db.Gifts
                    .Where(g => g.RecipientId == userId && g.Status == GiftStatus.Sent)
                    .ToListAsync();

                int claimed = 0;
                foreach (var gift in pending)
                {
                    try
                    {
                        if (gift.ExpiresAt.HasValue && gift.ExpiresAt.Value < now)
                        {
                            gift.Status = GiftStatus.Expired;
                        }
                        else
                        {
                            // Idempotent on the gift's correlation id — a retried claim never double-credits.
                            // CreditAsync commits in its own transaction; we persist THIS gift's status right
                            // after, so a mid-loop failure can never leave a credited gift marked Sent.
                            await _wallet.CreditAsync(userId.ToString(), gift.Currency, gift.Amount, TransactionType.Bonus,
                                gift.CorrelationId, new WalletContext { Description = "Gift claim" });
                            gift.Status = GiftStatus.Claimed;
                            gift.ClaimedAt = now;
                            claimed++;
                        }
                        await _db.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        // Drop the unsaved status flip and retry on the next claim (the credit is idempotent).
                        _logger.LogError(ex, "Gift claim failed for gift {GiftId} (user {UserId}).", gift.GiftId, userId);
                        _db.Entry(gift).State = EntityState.Detached;
                        if (gift.Status == GiftStatus.Claimed) claimed--;
                    }
                }
                return (true, null, claimed);
            }
            finally
            {
                await rdb.KeyDeleteAsync(claimLock);
            }
        }

        public async Task<IReadOnlyList<GiftDto>> GetPendingAsync(Guid userId)
        {
            var pending = await _db.Gifts.AsNoTracking()
                .Where(g => g.RecipientId == userId && g.Status == GiftStatus.Sent)
                .OrderByDescending(g => g.SentAt).Take(100).ToListAsync();

            var senderIds = pending.Select(g => g.SenderId).Distinct().ToList();
            var names = await _db.UserProfiles.AsNoTracking()
                .Where(p => senderIds.Contains(p.UserId))
                .ToDictionaryAsync(p => p.UserId, p => p.DisplayName);

            return pending.Select(g => new GiftDto(
                g.GiftId.ToString(), g.SenderId.ToString(),
                names.TryGetValue(g.SenderId, out var nm) ? nm : "Player",
                g.Amount, (int)g.Currency, (int)g.Status, g.SentAt)).ToList();
        }

        public async Task<int> RemainingTodayAsync(Guid userId)
        {
            var v = await _redis.GetDatabase().StringGetAsync(DailyKey(userId));
            int used = v.HasValue && int.TryParse(v, out var u) ? u : 0;
            return Math.Max(0, DailyGiftLimit - used);
        }

        private static string DailyKey(Guid userId) => $"gift:sent:{userId}:{DateTime.UtcNow:yyyyMMdd}";
    }
}
