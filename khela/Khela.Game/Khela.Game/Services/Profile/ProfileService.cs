using System;
using Khela.Common.Profiles;
using Khela.Common.Social;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Chat;
using Khela.Game.Services.Friends;
using Khela.Game.Services.Identity;
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

        /// <summary>Find a player by their case-insensitive Player ID and return their PUBLIC profile. Null if no
        /// such id, or blocked. Reuses the same block-aware public view as <see cref="GetPublicProfileAsync"/>.</summary>
        Task<PublicProfileDto> GetByPlayerIdAsync(Guid viewerId, string playerId);

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
            var perGame = await PerGameAsync(userId, includeNet: true);
            return new MyProfileDto
            {
                UserId = userId.ToString(),
                PlayerId = p.PublicId,
                DisplayName = p.DisplayName,
                AvatarId = p.AvatarId,
                AvatarFrameId = p.AvatarFrameId,
                CountryFlagId = p.CountryFlagId,
                Region = p.Region,
                Level = p.Level,
                Experience = p.LifetimeExperience,   // profile "total XP" = lifetime; the into-level bar comes from ProgressionDto
                VipTier = (int)p.VipTier,
                // LP lives in the wallet now (docs/VIP_SPEC.md §3); the profile column is retired and stays 0.
                LoyaltyPoints = (long)Math.Floor(await _db.PlayerWallets.AsNoTracking()
                    .Where(w => w.UserId == userId && w.Currency == CurrencyType.Lp).Select(w => (decimal?)w.Balance).FirstOrDefaultAsync() ?? 0m),
                Bio = p.Bio,
                StatusMessage = p.StatusMessage,
                CreatedAt = p.CreatedAt,
                LastSeenAt = p.LastSeenAt,
                FriendCount = p.FriendCount,
                Stats = BuildStats(p, perGame, includeNet: true),
                PerGame = perGame,
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

            var perGame = await PerGameAsync(targetId, includeNet: false);
            return new PublicProfileDto
            {
                UserId = targetId.ToString(),
                PlayerId = p.PublicId,
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
                Stats = BuildStats(p, perGame, includeNet: false),   // public view hides exact net worth
                PerGame = perGame,
                LinkedSocials = await PublicSocialsAsync(targetId),
            };
        }

        public async Task<PublicProfileDto> GetByPlayerIdAsync(Guid viewerId, string playerId)
        {
            var code = PublicPlayerId.Normalize(playerId);
            if (code == null) return null;
            var targetId = await _db.UserProfiles.AsNoTracking()
                .Where(p => p.PublicId == code)
                .Select(p => (Guid?)p.UserId)
                .FirstOrDefaultAsync();
            if (targetId == null) return null;
            return await GetPublicProfileAsync(viewerId, targetId.Value);   // reuse the block-aware public view
        }

        public async Task<(bool ok, string error)> UpdateAsync(Guid userId, UpdateProfileRequest req)
        {
            if (req == null) return (false, "Empty request.");

            var p = await _db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == userId);
            if (p == null)
            {
                // Same self-heal as the avatar save: registration's profile bootstrap swallows its own failures, so
                // an account can end up with no profile row and then be permanently unable to finish onboarding.
                // There is nothing here we can't create, so create it rather than refuse.
                var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.ToString());
                var region = (user?.CountryCode ?? "").Trim().ToUpperInvariant();
                if (region.Length != 2) region = "ZZ";
                var seed = string.IsNullOrWhiteSpace(user?.UserName)
                    ? $"Player{userId:N}".Substring(0, 12)
                    : user.UserName.Trim();
                if (seed.Length > 24) seed = seed.Substring(0, 24);

                p = new UserProfile
                {
                    UserId = userId,
                    PublicId = await PublicPlayerId.AllocateAsync(_db),   // permanent player id, unique
                    DisplayName = seed,
                    DisplayNameNormalized = seed.ToUpperInvariant(),
                    Region = region
                };
                _db.UserProfiles.Add(p);
                try { await _db.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    _db.ChangeTracker.Clear();   // lost a race — take the row the other request created
                    p = await _db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == userId);
                    if (p == null) return (false, "Could not create a profile for this account.");
                }
            }

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

            // ---- TimeZone: the player's own midnight for daily systems (the pass). Client-reported, so validate it
            //      against the tz database and IGNORE anything unknown — a stale or spoofed id must never break the
            //      profile save, it just leaves them on the previous zone (UTC by default). ----
            if (req.TimeZoneId != null)
            {
                var tz = req.TimeZoneId.Trim();
                if (tz.Length == 0)
                {
                    if (p.TimeZoneId != null) { p.TimeZoneId = null; changed = true; }
                }
                else if (tz.Length <= 64 && Khela.Game.Services.Pass.PassClock.IsKnown(tz) && p.TimeZoneId != tz)
                {
                    p.TimeZoneId = tz;
                    changed = true;
                }
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

        // The "All" aggregate is DERIVED from the per-game rows (UserGameStats — the authoritative per-game source),
        // NOT from UserProfile's separate counters. UserProfile can undercount: rounds that settled before the
        // profile row existed updated UserGameStats (which auto-creates its row) but hit the `if (profile != null)`
        // guard in PlayerStatsService and skipped the profile update — which made "All" show LESS than a single game.
        // Summing the per-game rows guarantees All == the true sum/extent of the games (≥ any one game).
        private static ProfileStatsDto BuildStats(UserProfile p, List<GameStatsDto> perGame, bool includeNet)
        {
            long gamesPlayed = 0, gamesWon = 0;
            decimal wagered = 0m, net = 0m, biggestWin = 0m;
            int longestStreak = 0;
            DateTime? lastPlayed = null, firstPlayed = null;
            foreach (var g in perGame)
            {
                gamesPlayed += g.GamesPlayed;
                gamesWon += g.GamesWon;
                wagered += g.TotalWagered;
                net += g.NetProfit ?? 0m;
                if (g.BiggestWin > biggestWin) biggestWin = g.BiggestWin;
                if (g.LongestWinStreak > longestStreak) longestStreak = g.LongestWinStreak;
                if (g.LastPlayedAt.HasValue && (lastPlayed == null || g.LastPlayedAt > lastPlayed)) lastPlayed = g.LastPlayedAt;
                if (g.StartedPlayingAt.HasValue && (firstPlayed == null || g.StartedPlayingAt < firstPlayed)) firstPlayed = g.StartedPlayingAt;
            }
            return new ProfileStatsDto
            {
                GamesPlayed = gamesPlayed,
                GamesWon = gamesWon,
                WinRate = gamesPlayed > 0 ? Math.Round(100.0 * gamesWon / gamesPlayed, 1) : 0,
                BiggestWin = biggestWin,
                CurrentWinStreak = p.CurrentWinStreak,                        // cross-game CURRENT streak only lives on UserProfile
                LongestWinStreak = Math.Max(longestStreak, p.LongestWinStreak),
                NetProfit = includeNet ? net : (decimal?)null,
                TotalWagered = wagered,
                LastPlayedAt = lastPlayed ?? p.LastPlayedAt,
                StartedPlayingAt = firstPlayed ?? p.CreatedAt,
            };
        }

        // Per-game stat rows (one per game the player has played), newest-played first. NetProfit own-only.
        private async Task<List<GameStatsDto>> PerGameAsync(Guid userId, bool includeNet)
        {
            var rows = await _db.UserGameStats.AsNoTracking()
                .Where(s => s.UserId == userId)
                .OrderByDescending(s => s.LastPlayedAt)
                .ToListAsync();
            return rows.Select(s => new GameStatsDto
            {
                Game = (int)s.GameType,
                DisplayName = GameDisplayName(s.GameType),
                GamesPlayed = s.GamesPlayed,
                GamesWon = s.GamesWon,
                WinRate = s.GamesPlayed > 0 ? Math.Round(100.0 * s.GamesWon / s.GamesPlayed, 1) : (double?)null,
                TotalWagered = s.TotalWagered,
                BiggestWin = s.BiggestSingleWin,
                NetProfit = includeNet ? s.NetProfit : (decimal?)null,
                CurrentWinStreak = s.CurrentWinStreak,
                LongestWinStreak = s.LongestWinStreak,
                ExperienceEarned = s.ExperienceEarned,
                LastPlayedAt = s.LastPlayedAt,
                StartedPlayingAt = s.FirstPlayedAt,
                StatCounters = BuildStatCounters(s.GameType, s.StatCountersJson),
            }).ToList();
        }

        // The per-game stat-counter panel: the catalog's ordered (key,label) joined with the stored JSON bag
        // (value 0 if the player hasn't logged that stat yet), so the client renders a complete, ordered list.
        private static List<Khela.Common.Stats.StatCounterDto> BuildStatCounters(
            Khela.Common.Leaderboards.GameType game, string json)
        {
            var catalog = Khela.Common.Stats.GameStatCatalog.For(game);
            if (catalog.Count == 0) return new List<Khela.Common.Stats.StatCounterDto>();

            Dictionary<string, long> bag = null;
            if (!string.IsNullOrEmpty(json))
            {
                try { bag = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(json); }
                catch { /* corrupt bag → treat as empty */ }
            }
            var result = new List<Khela.Common.Stats.StatCounterDto>(catalog.Count);
            foreach (var (key, label) in catalog)
                result.Add(new Khela.Common.Stats.StatCounterDto
                {
                    Key = key,
                    Label = label,
                    Value = bag != null && bag.TryGetValue(key, out var v) ? v : 0L,
                });
            return result;
        }

        private static string GameDisplayName(Khela.Common.Leaderboards.GameType g) => g switch
        {
            Khela.Common.Leaderboards.GameType.Blackjack => "Blackjack",
            Khela.Common.Leaderboards.GameType.Poker     => "Poker",
            Khela.Common.Leaderboards.GameType.TeenPatti => "Teen Patti",
            Khela.Common.Leaderboards.GameType.Roulette  => "Roulette",
            Khela.Common.Leaderboards.GameType.General   => "General",
            _ => g.ToString()
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
