using Khela.Common.Social;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Presence;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Services.Friends
{
    public sealed record FriendDto(string UserId, string DisplayName, string AvatarId, string Region, bool IsOnline, int Status);

    public interface IFriendsService
    {
        Task<(bool ok, string error)> SendRequestAsync(Guid requesterId, Guid addresseeId);
        Task<(bool ok, string error)> RespondAsync(Guid userId, Guid requesterId, bool accept);
        Task<(bool ok, string error)> RemoveAsync(Guid userId, Guid otherId);
        Task<(bool ok, string error)> BlockAsync(Guid userId, Guid otherId);
        Task<IReadOnlyList<FriendDto>> GetFriendsAsync(Guid userId);
        Task<IReadOnlyList<FriendDto>> GetPendingAsync(Guid userId);
        Task<IReadOnlyList<FriendDto>> SearchAsync(Guid userId, string query, int limit = 20);
        Task<IReadOnlyList<FriendDto>> RecentPlayersAsync(Guid userId, int limit = 20);
    }

    /// <summary>
    /// Friend graph + discovery. Friendship is one row per request (Requester->Addressee); an Accepted row
    /// IS the friendship (queried both directions). Recent-players is DERIVED from GameHandParticipant so a
    /// brand-new user with no friends still has people to add. Online status comes from PresenceService.
    /// </summary>
    public sealed class FriendsService : IFriendsService
    {
        private readonly AppDbContext _db;
        private readonly IPresenceService _presence;

        public FriendsService(AppDbContext db, IPresenceService presence)
        {
            _db = db;
            _presence = presence;
        }

        public async Task<(bool, string)> SendRequestAsync(Guid requesterId, Guid addresseeId)
        {
            if (requesterId == addresseeId) return (false, "You can't add yourself.");

            var existing = await _db.Friendships.FirstOrDefaultAsync(f =>
                (f.RequesterId == requesterId && f.AddresseeId == addresseeId) ||
                (f.RequesterId == addresseeId && f.AddresseeId == requesterId));

            if (existing != null)
            {
                switch (existing.Status)
                {
                    case FriendshipStatus.Accepted: return (false, "Already friends.");
                    case FriendshipStatus.Pending:  return (false, "A request is already pending.");
                    case FriendshipStatus.Blocked:  return (false, "Unavailable.");
                    default: // Declined -> allow a fresh request by re-pointing the existing edge
                        existing.RequesterId = requesterId;
                        existing.AddresseeId = addresseeId;
                        existing.Status = FriendshipStatus.Pending;
                        existing.RespondedAt = null;
                        existing.CreatedAt = DateTime.UtcNow;
                        return await SaveRequestAsync();
                }
            }

            _db.Friendships.Add(new Friendship
            {
                RequesterId = requesterId,
                AddresseeId = addresseeId,
                Status = FriendshipStatus.Pending
            });
            return await SaveRequestAsync();
        }

        // A concurrent duplicate request can race the (RequesterId, AddresseeId) unique index; translate the
        // collision into the same clean result instead of letting a raw DbUpdateException surface as a 500.
        private async Task<(bool, string)> SaveRequestAsync()
        {
            try
            {
                await _db.SaveChangesAsync();
                return (true, null);
            }
            catch (DbUpdateException)
            {
                return (false, "A request is already pending.");
            }
        }

        public async Task<(bool, string)> RespondAsync(Guid userId, Guid requesterId, bool accept)
        {
            var req = await _db.Friendships.FirstOrDefaultAsync(f =>
                f.RequesterId == requesterId && f.AddresseeId == userId && f.Status == FriendshipStatus.Pending);
            if (req == null) return (false, "No pending request.");

            req.Status = accept ? FriendshipStatus.Accepted : FriendshipStatus.Declined;
            req.RespondedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return (true, null);
        }

        public async Task<(bool, string)> RemoveAsync(Guid userId, Guid otherId)
        {
            var edge = await _db.Friendships.FirstOrDefaultAsync(f =>
                ((f.RequesterId == userId && f.AddresseeId == otherId) ||
                 (f.RequesterId == otherId && f.AddresseeId == userId)) && f.Status == FriendshipStatus.Accepted);
            if (edge == null) return (false, "Not friends.");

            _db.Friendships.Remove(edge);
            await _db.SaveChangesAsync();
            return (true, null);
        }

        public async Task<(bool, string)> BlockAsync(Guid userId, Guid otherId)
        {
            if (userId == otherId) return (false, "Invalid.");

            var edge = await _db.Friendships.FirstOrDefaultAsync(f =>
                (f.RequesterId == userId && f.AddresseeId == otherId) ||
                (f.RequesterId == otherId && f.AddresseeId == userId));

            if (edge == null)
            {
                _db.Friendships.Add(new Friendship { RequesterId = userId, AddresseeId = otherId, Status = FriendshipStatus.Blocked });
            }
            else
            {
                edge.RequesterId = userId;   // the blocker owns the edge
                edge.AddresseeId = otherId;
                edge.Status = FriendshipStatus.Blocked;
                edge.RespondedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            return (true, null);
        }

        public async Task<IReadOnlyList<FriendDto>> GetFriendsAsync(Guid userId)
        {
            var edges = await _db.Friendships.AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == userId || f.AddresseeId == userId))
                .ToListAsync();
            var otherIds = edges.Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId).Distinct().ToList();
            return await ToDtosAsync(otherIds, (int)FriendshipStatus.Accepted);
        }

        public async Task<IReadOnlyList<FriendDto>> GetPendingAsync(Guid userId)
        {
            var ids = await _db.Friendships.AsNoTracking()
                .Where(f => f.AddresseeId == userId && f.Status == FriendshipStatus.Pending)
                .Select(f => f.RequesterId).ToListAsync();
            return await ToDtosAsync(ids, (int)FriendshipStatus.Pending);
        }

        public async Task<IReadOnlyList<FriendDto>> SearchAsync(Guid userId, string query, int limit = 20)
        {
            limit = Math.Clamp(limit, 1, 50);
            query = (query ?? "").Trim();
            if (query.Length < 2) return Array.Empty<FriendDto>();

            var ids = new List<Guid>();
            if (Guid.TryParse(query, out var byId)) ids.Add(byId);

            var norm = query.ToUpperInvariant();
            var byName = await _db.UserProfiles.AsNoTracking()
                .Where(p => p.UserId != userId && p.DisplayNameNormalized.Contains(norm))
                .OrderBy(p => p.DisplayNameNormalized)
                .Select(p => p.UserId).Take(limit).ToListAsync();
            ids.AddRange(byName);

            return await ToDtosAsync(ids.Distinct().Take(limit).ToList(), -1);
        }

        public async Task<IReadOnlyList<FriendDto>> RecentPlayersAsync(Guid userId, int limit = 20)
        {
            limit = Math.Clamp(limit, 1, 50);

            // Distinct co-players from the user's settled hands, ordered by the most recent shared settle time,
            // so the genuinely-most-recent opponents are kept (a plain DISTINCT+Take has no defined order).
            var otherIds = await (
                from p in _db.GameHandParticipants.AsNoTracking()
                join h in _db.GameHandHeaders.AsNoTracking() on p.HandId equals h.HandId
                where h.Status == HandStatus.Settled && h.SettledAt != null && p.UserId != userId
                      && _db.GameHandParticipants.Any(me => me.HandId == h.HandId && me.UserId == userId)
                group h by p.UserId into g
                orderby g.Max(x => x.SettledAt) descending
                select g.Key)
                .Take(limit)
                .ToListAsync();

            return await ToDtosAsync(otherIds, -1);
        }

        private async Task<IReadOnlyList<FriendDto>> ToDtosAsync(List<Guid> userIds, int status)
        {
            if (userIds.Count == 0) return Array.Empty<FriendDto>();

            var profiles = (await _db.UserProfiles.AsNoTracking()
                    .Where(p => userIds.Contains(p.UserId))
                    .Select(p => new { p.UserId, p.DisplayName, p.AvatarId, p.Region })
                    .ToListAsync())
                .ToDictionary(p => p.UserId);

            var online = await _presence.FilterOnlineAsync(userIds);

            // Project from the requested ids (preserving caller order) so a friend/search hit whose profile
            // row is missing is still surfaced with a placeholder rather than silently dropped.
            return userIds.Select(id =>
            {
                profiles.TryGetValue(id, out var p);
                return new FriendDto(
                    id.ToString(),
                    p?.DisplayName ?? "Player",
                    p?.AvatarId ?? "default",
                    p?.Region ?? "ZZ",
                    online.Contains(id),
                    status);
            }).ToList();
        }
    }
}
