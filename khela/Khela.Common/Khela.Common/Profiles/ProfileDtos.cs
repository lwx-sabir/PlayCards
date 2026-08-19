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
        public decimal TotalWagered { get; set; }
        public DateTime? LastPlayedAt { get; set; }
        /// <summary>When the player first played any game (earliest per-game FirstPlayedAt, else profile CreatedAt).</summary>
        public DateTime? StartedPlayingAt { get; set; }
    }

    /// <summary>Per-game stats for one game the player has played — the "Blackjack" / "Slots" / … tabs. Field
    /// names mirror <see cref="ProfileStatsDto"/> so the same UI stat-block binds to the "All" tab or a game tab.
    /// <see cref="NetProfit"/> is own-profile only (null on public); <see cref="WinRate"/> is null when there are
    /// no games yet (or for games with no per-hand win/loss, e.g. slots).</summary>
    public class GameStatsDto
    {
        public int Game { get; set; }                    // Khela.Common.Leaderboards.GameType as int
        public string DisplayName { get; set; } = string.Empty;
        public long GamesPlayed { get; set; }
        public long GamesWon { get; set; }
        public double? WinRate { get; set; }             // percentage 0..100 (1 dp); null = n/a
        public decimal TotalWagered { get; set; }
        public decimal BiggestWin { get; set; }
        public decimal? NetProfit { get; set; }
        public int CurrentWinStreak { get; set; }
        public int LongestWinStreak { get; set; }
        public long ExperienceEarned { get; set; }
        public DateTime? LastPlayedAt { get; set; }
        public DateTime? StartedPlayingAt { get; set; }  // = UserGameStats.FirstPlayedAt

        /// <summary>Game-specific lifetime stat counters (the "playstyle" stats — blackjacks, doubles, busts, …),
        /// in catalog order (label + value). Built server-side from the JSON bag on UserGameStats joined with
        /// <see cref="Khela.Common.Stats.GameStatCatalog"/>, so the client just renders the list. Empty for games
        /// that declare no counters.</summary>
        public List<Khela.Common.Stats.StatCounterDto> StatCounters { get; set; } = new List<Khela.Common.Stats.StatCounterDto>();
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
        /// <summary>Public, permanent, searchable "Player ID" (8-char alphanumeric). Never changes.</summary>
        public string PlayerId { get; set; } = string.Empty;
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
        /// <summary>Per-game breakdown (one per game played), newest-played first — the per-game stat tabs.</summary>
        public List<GameStatsDto> PerGame { get; set; } = new List<GameStatsDto>();
        public List<LinkedSocialDto> LinkedSocials { get; set; } = new List<LinkedSocialDto>();
    }

    /// <summary>Another player's PUBLIC profile — excludes account/contact fields and exact net worth.</summary>
    public class PublicProfileDto
    {
        public string UserId { get; set; } = string.Empty;
        /// <summary>Public, permanent, searchable "Player ID" (8-char alphanumeric). Never changes.</summary>
        public string PlayerId { get; set; } = string.Empty;
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
        /// <summary>Per-game breakdown (one per game played), newest-played first. NetProfit null on public.</summary>
        public List<GameStatsDto> PerGame { get; set; } = new List<GameStatsDto>();
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

        /// <summary>The device's IANA timezone ("Asia/Dhaka"), sent at login. Daily systems (the pass) roll over at
        /// the player's LOCAL midnight, so without this everyone falls back to UTC — a Dhaka player's day would end
        /// at 6am. Validated server-side; an unknown id is ignored rather than rejected.</summary>
        public string TimeZoneId { get; set; }
    }
}
