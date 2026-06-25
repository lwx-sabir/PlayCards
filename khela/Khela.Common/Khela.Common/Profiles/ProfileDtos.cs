using System;
using System.Collections.Generic;

namespace Khela.Common.Profiles
{
    /// <summary>Aggregate game stats shown on a profile. <see cref="NetProfit"/> is own-profile only (null on public).</summary>
    public class ProfileStatsDto
    {
        public long GamesPlayed { get; set; }
        public long GamesWon { get; set; }
        /// <summary>Derived win rate as a percentage (0..100), rounded to 1 dp.</summary>
        public double WinRate { get; set; }
        public decimal BiggestWin { get; set; }
        public int CurrentWinStreak { get; set; }
        public int LongestWinStreak { get; set; }
        /// <summary>Lifetime net (signed). Returned on the OWN profile only — omitted (null) on public profiles.</summary>
        public decimal? NetProfit { get; set; }
    }

    /// <summary>A publicly-shown linked social account.</summary>
    public class LinkedSocialDto
    {
        public LinkedAccountProvider Provider { get; set; }
        public string Handle { get; set; }
    }

    /// <summary>The caller's own full profile (includes private-ish fields like NetProfit).</summary>
    public class MyProfileDto
    {
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string AvatarId { get; set; }
        public string AvatarFrameId { get; set; }
        public string CountryFlagId { get; set; }
        public string Region { get; set; } = "ZZ";
        public int Level { get; set; }
        public long Experience { get; set; }
        public int VipTier { get; set; }
        public long LoyaltyPoints { get; set; }
        public string Bio { get; set; }
        public string StatusMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public int FriendCount { get; set; }
        public ProfileStatsDto Stats { get; set; } = new ProfileStatsDto();
        public List<LinkedSocialDto> LinkedSocials { get; set; } = new List<LinkedSocialDto>();
    }

    /// <summary>Another player's PUBLIC profile — excludes account/contact fields and exact net worth.</summary>
    public class PublicProfileDto
    {
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string AvatarId { get; set; }
        public string AvatarFrameId { get; set; }
        public string CountryFlagId { get; set; }
        public string Region { get; set; } = "ZZ";
        public int Level { get; set; }
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
        public ProfileStatsDto Stats { get; set; } = new ProfileStatsDto();
        public List<LinkedSocialDto> LinkedSocials { get; set; } = new List<LinkedSocialDto>();
    }

    /// <summary>Profile edit. A null field = leave unchanged; for Bio/StatusMessage an empty string clears it.</summary>
    public class UpdateProfileRequest
    {
        public string DisplayName { get; set; }
        public string AvatarId { get; set; }
        public string AvatarFrameId { get; set; }
        public string CountryFlagId { get; set; }
        public string Bio { get; set; }
        public string StatusMessage { get; set; }
    }
}
