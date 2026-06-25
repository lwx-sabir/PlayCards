using System;
using System.Collections.Generic;

namespace PlayCard.Game.Net
{
    /// <summary>
    /// Client mirror of the server's MyProfileDto (GET /api/profile/me) — the signed-in player's authoritative
    /// profile. Held by ProfileManager. The server is the source of truth; this is a display cache. Kept as a
    /// standalone client type (like <see cref="WalletBalances"/>) so the client doesn't depend on Khela.Common.
    /// </summary>
    public sealed class UserProfileData
    {
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";

        // Cosmetics — catalog ids; the client maps id → asset (avatar image, frame/border, country flag).
        public string AvatarId { get; set; }
        public string AvatarFrameId { get; set; }
        public string CountryFlagId { get; set; }

        public string Region { get; set; } = "ZZ";   // ISO-3166 alpha-2, "ZZ" = unknown

        // Progression / VIP.
        public int Level { get; set; } = 1;
        public long Experience { get; set; }          // XP toward next level
        public int VipTier { get; set; }
        public long LoyaltyPoints { get; set; }

        // Blurbs (user-editable, moderated server-side).
        public string Bio { get; set; }
        public string StatusMessage { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public int FriendCount { get; set; }

        public ProfileStats Stats { get; set; } = new ProfileStats();
        public List<LinkedSocial> LinkedSocials { get; set; } = new List<LinkedSocial>();
    }

    /// <summary>Aggregate stats block on a profile. <see cref="NetProfit"/> is own-profile only (null elsewhere).</summary>
    public sealed class ProfileStats
    {
        public long GamesPlayed { get; set; }
        public long GamesWon { get; set; }
        public double WinRate { get; set; }          // 0..100, one decimal
        public decimal BiggestWin { get; set; }
        public int CurrentWinStreak { get; set; }
        public int LongestWinStreak { get; set; }
        public decimal? NetProfit { get; set; }      // lifetime net, own profile only
    }

    /// <summary>A linked social account shown on the profile (Provider id + handle).</summary>
    public sealed class LinkedSocial
    {
        public int Provider { get; set; }
        public string Handle { get; set; }
    }

    /// <summary>
    /// Client mirror of the server's PublicProfileDto (GET /api/profile/{userId}) — ANOTHER player's public profile.
    /// Excludes account/contact fields and exact net worth; adds the viewer-relationship flags. Returned block-aware
    /// (the server gives 404 if either party has blocked the other).
    /// </summary>
    public sealed class PublicProfileData
    {
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AvatarId { get; set; }
        public string AvatarFrameId { get; set; }
        public string CountryFlagId { get; set; }
        public string Region { get; set; } = "ZZ";
        public int Level { get; set; } = 1;
        public int VipTier { get; set; }
        public string Bio { get; set; }
        public string StatusMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public int FriendCount { get; set; }
        public bool IsOnline { get; set; }
        // Relationship to the viewer.
        public bool IsFriend { get; set; }
        public bool RequestFromMePending { get; set; }
        public bool RequestToMePending { get; set; }
        public ProfileStats Stats { get; set; } = new ProfileStats();
        public List<LinkedSocial> LinkedSocials { get; set; } = new List<LinkedSocial>();
    }

    /// <summary>
    /// Profile edit body (PATCH /api/profile/me). A null field = leave unchanged; an empty string clears Bio/Status.
    /// DisplayName + cosmetics are server-validated/moderated, so re-pull the profile after a successful edit.
    /// </summary>
    public sealed class ProfileEditRequest
    {
        public string DisplayName { get; set; }
        public string AvatarId { get; set; }
        public string AvatarFrameId { get; set; }
        public string CountryFlagId { get; set; }
        public string Bio { get; set; }
        public string StatusMessage { get; set; }
    }

    /// <summary>
    /// Client mirror of the server's ProgressionDto (GET /api/progression/me) — the live XP-bar state.
    /// <see cref="Xp"/> is INTO-LEVEL progress, so the bar fill = Xp / XpToNext.
    /// </summary>
    public sealed class ProgressionData
    {
        public int Level { get; set; } = 1;
        public long Xp { get; set; }                // into-level XP (0..XpToNext)
        public long XpToNext { get; set; }          // XP needed to reach the next level (bar denominator)
        public long DailyXpRemaining { get; set; }  // XP still earnable today before the cap
    }
}
