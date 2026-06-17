using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Leaderboards;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// The GAME profile, 1:1 with ApplicationUser (by Guid UserId, loose-coupled like PlayerWallet —
    /// no EF navigation). Holds in-game identity, progression, VIP/loyalty, social counters, and the
    /// cross-game ("General") lifetime aggregate. Per-game numbers live in <see cref="UserGameStats"/>.
    /// Deliberately does NOT duplicate ApplicationUser contact/address/auth fields.
    /// </summary>
    [Table("UserProfiles")]
    [Index(nameof(UserId), IsUnique = true)]                // 1:1 enforcement + lookup
    [Index(nameof(DisplayNameNormalized), IsUnique = true)] // unique case-folded display name
    [Index(nameof(Region))]                                 // regional board membership scans
    [Index(nameof(VipTier))]                                // VIP filters / ops dashboards
    public class UserProfile
    {
        [Key]
        public Guid ProfileId { get; set; } = Guid.NewGuid();

        /// <summary>FK to AspNetUsers.Id (string Identity Id parsed to Guid). No EF navigation.</summary>
        [Required]
        public Guid UserId { get; set; }

        // ---- In-game identity (distinct from ApplicationUser contact fields) ----
        [Required, MaxLength(32)]
        public string DisplayName { get; set; }

        /// <summary>Case-folded copy of DisplayName (store ToUpperInvariant()) for the unique index.</summary>
        [Required, MaxLength(32)]
        public string DisplayNameNormalized { get; set; }

        [MaxLength(256)]
        public string AvatarId { get; set; }       // catalog id, not ApplicationUser.ProfilePicture

        [MaxLength(128)]
        public string AvatarFrameId { get; set; }  // equipped cosmetic frame/border

        [MaxLength(64)]
        public string CountryFlagId { get; set; }  // optional cosmetic flag (may differ from Region)

        // ---- Region (denormalized so board writes/reads never join ApplicationUser) ----
        /// <summary>ISO-3166 alpha-2, UPPER. "ZZ" = unknown.</summary>
        [Required, MaxLength(2), Column(TypeName = "char(2)")]
        public string Region { get; set; } = "ZZ";

        // ---- Progression ----
        [Required] public int  Level { get; set; } = 1;
        [Required] public long Experience { get; set; } = 0;          // XP toward next level (resets on level-up)
        [Required] public long LifetimeExperience { get; set; } = 0;  // monotonic; safe XP-board source

        // ---- VIP / loyalty ----
        [Required] public VipTier VipTier { get; set; } = VipTier.None;
        [Required] public long LoyaltyPoints { get; set; } = 0;       // current spendable balance
        [Required] public long LifetimeLoyaltyPoints { get; set; } = 0;

        // ---- Lifetime CROSS-GAME aggregates (General board + home screen) ----
        [Required] public long GamesPlayed { get; set; } = 0;
        [Required] public long GamesWon    { get; set; } = 0;

        // 28,4 (not 18,4): lifetime monotonic accumulators, widened to match LeaderboardArchiveEntry.Score.
        [Precision(28, 4)] public decimal TotalWagered { get; set; } = 0m;
        [Precision(28, 4)] public decimal TotalWon     { get; set; } = 0m; // gross chips won
        [Precision(28, 4)] public decimal NetProfit    { get; set; } = 0m; // TotalWon - TotalWagered, signed
        [Precision(18, 4)] public decimal BiggestWin   { get; set; } = 0m; // max single win (single, not a sum)

        [Required] public int CurrentWinStreak  { get; set; } = 0;
        [Required] public int LongestWinStreak  { get; set; } = 0;
        [Required] public int CurrentLoseStreak { get; set; } = 0;
        [Required] public int LongestLoseStreak { get; set; } = 0;

        // ---- Social counters (denormalized; source of truth is friends/referral tables) ----
        [Required] public int ReferralCount { get; set; } = 0;
        [Required] public int FriendCount   { get; set; } = 0;

        // ---- Game preferences (multi-game) ----
        /// <summary>The game that opens first / the player's home game. Null = no preference yet.</summary>
        public Khela.Common.Leaderboards.GameType? DefaultGame { get; set; }

        /// <summary>Most recently played game + when — drives "jump back in".
        /// Favourites and most-played live per-game on UserGameStats (IsFavorite / GamesPlayed).</summary>
        public Khela.Common.Leaderboards.GameType? LastPlayedGameType { get; set; }
        public DateTime? LastPlayedAt { get; set; }

        // ---- Timestamps ----
        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [Required] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastSeenAt { get; set; }

        // Concurrency token — MySQL timestamp(6) rowversion. NEVER byte[]; keep DateTime?.
        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
