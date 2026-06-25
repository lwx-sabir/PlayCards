using Khela.Common.Profiles;
using Khela.Common.Social;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Chat;
using Khela.Game.Services.Friends;
using Khela.Game.Services.Presence;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Services.Profile
{
    public interface IProfileService
    {
        /// <summary>The caller's own full profile (null if no profile row).</summary>
        Task<MyProfileDto> GetMyProfileAsync(Guid userId);

        /// <summary>Another player's PUBLIC profile. Null if not found OR blocked in either direction.</summary>
        Task<PublicProfileDto> GetPublicProfileAsync(Guid viewerId, Guid targetId);

        /// <summary>Edit the caller's profile (moderated names/blurbs, unique name, owned-cosmetics gate).</summary>
        Task<(bool ok, string error)> UpdateAsync(Guid userId, UpdateProfileRequest req);

        /// <summary>Stamp LastSeenAt = now (called when the user's last connection drops).</summary>
        Task SetLastSeenAsync(Guid userId);
    }

    /// <summary>
    /// Read/edit of the game profile. Names + blurbs go through <see cref="IChatModerator"/> on write; the public
    /// view is block-aware (via <see cref="IFriendsService.IsBlockedBetweenAsync"/>) and drops account/contact
    /// fields and exact net worth. Cosmetics equips are gated to a free/default set until an ownership table exists.
    /// </summary>
    public sealed class ProfileService : IProfileService
    {
        private readonly AppDbContext _db;
        private readonly IPresenceService _presence;
        private readonly IFriendsService _friends;
        private readonly IChatModerator _moderator;

        public ProfileService(AppDbContext db, IPresenceService presence, IFriendsService friends, IChatModerator moderator)
        {
            _db = db;
            _presence = presence;
            _friends = friends;
            _moderator = moderator;
        }

        // TODO(cosmetics): replace with a real entitlements/ownership check once a cosmetics catalog + inventory
        // table exists. Until then ONLY the free/default set (or clearing) may be equipped, so a client can't equip
        // arbitrary unowned ids. Expand these to match the shipped default catalog (incl. country flags).
        private static readonly HashSet<string> FreeAvatars = new(StringComparer.OrdinalIgnoreCase) { "default", "avatar_default" };
        private static readonly HashSet<string> FreeFrames  = new(StringComparer.OrdinalIgnoreCase) { "default", "frame_default" };
        private static readonly HashSet<string> FreeFlags   = new(StringComparer.OrdinalIgnoreCase) { "default", "flag_default" };

        public async Task<MyProfileDto> GetMyProfileAsync(Guid userId)
        {
            var p = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId);
            if (p == null) return null;
            return new MyProfileDto
            {
                UserId = userId.ToString(),
                DisplayName = p.DisplayName,
                AvatarId = p.AvatarId,
                AvatarFrameId = p.AvatarFrameId,
                CountryFlagId = p.CountryFlagId,
                Region = p.Region,
                Level = p.Level,
                Experience = p.Experience,
                VipTier = (int)p.VipTier,
                LoyaltyPoints = p.LoyaltyPoints,
                Bio = p.Bio,
                StatusMessage = p.StatusMessage,
                CreatedAt = p.CreatedAt,
                LastSeenAt = p.LastSeenAt,
                FriendCount = p.FriendCount,
                Stats = BuildStats(p, includeNet: true),
                LinkedSocials = await PublicSocialsAsync(userId),
            };
        }

        public async Task<PublicProfileDto> GetPublicProfileAsync(Guid viewerId, Guid targetId)
        {
            // Block-aware: a Blocked edge in either direction hides the profile entirely.
            if (viewerId != targetId && await _friends.IsBlockedBetweenAsync(viewerId, targetId)) return null;

            var p = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == targetId);
            if (p == null) return null;

            bool isFriend = false, fromMe = false, toMe = false;
            if (viewerId != targetId)
            {
                var edge = await _db.Friendships.AsNoTracking().FirstOrDefaultAsync(f =>
                    (f.RequesterId == viewerId && f.AddresseeId == targetId) ||
                    (f.RequesterId == targetId && f.AddresseeId == viewerId));
                if (edge != null)
                {
                    if (edge.Status == FriendshipStatus.Accepted) isFriend = true;
                    else if (edge.Status == FriendshipStatus.Pending)
                    {
                        if (edge.RequesterId == viewerId) fromMe = true; else toMe = true;
                    }
                }
            }

            return new PublicProfileDto
            {
                UserId = targetId.ToString(),
                DisplayName = p.DisplayName,
                AvatarId = p.AvatarId,
                AvatarFrameId = p.AvatarFrameId,
                CountryFlagId = p.CountryFlagId,
                Region = p.Region,
                Level = p.Level,
                VipTier = (int)p.VipTier,
                Bio = p.Bio,
                StatusMessage = p.StatusMessage,
                CreatedAt = p.CreatedAt,
                LastSeenAt = p.LastSeenAt,
                FriendCount = p.FriendCount,
                IsOnline = await _presence.IsOnlineAsync(targetId),
                IsFriend = isFriend,
                RequestFromMePending = fromMe,
                RequestToMePending = toMe,
                Stats = BuildStats(p, includeNet: false),   // public view hides exact net worth
                LinkedSocials = await PublicSocialsAsync(targetId),
            };
        }

        public async Task<(bool ok, string error)> UpdateAsync(Guid userId, UpdateProfileRequest req)
        {
            if (req == null) return (false, "Empty request.");

            var p = await _db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == userId);
            if (p == null) return (false, "Profile not found.");

            bool changed = false;

            // ---- DisplayName: 3–32, clean (no masked words), unique (case-folded) ----
            if (req.DisplayName != null)
            {
                var name = req.DisplayName.Trim();
                if (name.Length < 3 || name.Length > 32) return (false, "Display name must be 3–32 characters.");
                var (ok, text) = await ModerateAsync(name, nameRules: true);
                if (!ok) return (false, "Display name contains disallowed content.");
                name = text.Trim();
                var norm = name.ToUpperInvariant();
                if (norm != p.DisplayNameNormalized)
                {
                    if (await _db.UserProfiles.AnyAsync(x => x.UserId != userId && x.DisplayNameNormalized == norm))
                        return (false, "That display name is taken.");
                    p.DisplayName = name;
                    p.DisplayNameNormalized = norm;
                    changed = true;
                }
            }

            // ---- Cosmetics: only owned (currently free/default set) may be equipped; empty clears ----
            if (req.AvatarId != null)
            {
                if (!CanEquip(req.AvatarId, FreeAvatars)) return (false, "You don't own that avatar.");
                p.AvatarId = NullIfEmpty(req.AvatarId); changed = true;
            }
            if (req.AvatarFrameId != null)
            {
                if (!CanEquip(req.AvatarFrameId, FreeFrames)) return (false, "You don't own that frame.");
                p.AvatarFrameId = NullIfEmpty(req.AvatarFrameId); changed = true;
            }
            if (req.CountryFlagId != null)
            {
                if (!CanEquip(req.CountryFlagId, FreeFlags)) return (false, "You don't own that flag.");
                p.CountryFlagId = NullIfEmpty(req.CountryFlagId); changed = true;
            }

            // ---- Bio / StatusMessage: moderated; empty clears ----
            if (req.Bio != null)
            {
                if (string.IsNullOrWhiteSpace(req.Bio)) p.Bio = null;
                else
                {
                    if (req.Bio.Trim().Length > 160) return (false, "Bio is too long (160 max).");
                    var (ok, text) = await ModerateAsync(req.Bio, nameRules: false);
                    if (!ok) return (false, "Bio contains disallowed content.");
                    p.Bio = text;
                }
                changed = true;
            }
            if (req.StatusMessage != null)
            {
                if (string.IsNullOrWhiteSpace(req.StatusMessage)) p.StatusMessage = null;
                else
                {
                    if (req.StatusMessage.Trim().Length > 80) return (false, "Status is too long (80 max).");
                    var (ok, text) = await ModerateAsync(req.StatusMessage, nameRules: false);
                    if (!ok) return (false, "Status contains disallowed content.");
                    p.StatusMessage = text;
                }
                changed = true;
            }

            if (!changed) return (true, null);
            p.UpdatedAt = DateTime.UtcNow;
            try
            {
                await _db.SaveChangesAsync();   // RowVersion is the concurrency token (timestamp(6))
            }
            catch (DbUpdateConcurrencyException)
            {
                return (false, "Profile was modified elsewhere — please retry.");
            }
            catch (DbUpdateException)
            {
                return (false, "That display name is taken.");   // unique-index race on DisplayNameNormalized
            }
            return (true, null);
        }

        public Task SetLastSeenAsync(Guid userId)
            => _db.UserProfiles.Where(p => p.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSeenAt, DateTime.UtcNow));

        // ---- helpers ----

        private static ProfileStatsDto BuildStats(UserProfile p, bool includeNet) => new ProfileStatsDto
        {
            GamesPlayed = p.GamesPlayed,
            GamesWon = p.GamesWon,
            WinRate = p.GamesPlayed > 0 ? Math.Round(100.0 * p.GamesWon / p.GamesPlayed, 1) : 0,
            BiggestWin = p.BiggestWin,
            CurrentWinStreak = p.CurrentWinStreak,
            LongestWinStreak = p.LongestWinStreak,
            NetProfit = includeNet ? p.NetProfit : (decimal?)null,
        };

        private Task<List<LinkedSocialDto>> PublicSocialsAsync(Guid userId)
            => _db.UserLinkedAccounts.AsNoTracking()
                .Where(a => a.UserId == userId && a.IsPublic)
                .Select(a => new LinkedSocialDto { Provider = a.Provider, Handle = a.Handle })
                .ToListAsync();

        // Moderates input; returns the (possibly masked) text. For names a Masked result is treated as a
        // rejection — a display name must be fully clean, not starred out.
        private async Task<(bool ok, string text)> ModerateAsync(string input, bool nameRules)
        {
            var mod = await _moderator.ModerateAsync(input);
            if (mod.Outcome == ModerationOutcome.Rejected) return (false, null);
            if (nameRules && mod.Outcome == ModerationOutcome.Masked) return (false, null);
            return (true, mod.Text);
        }

        private static bool CanEquip(string id, HashSet<string> free)
            => string.IsNullOrWhiteSpace(id) || free.Contains(id.Trim());

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
