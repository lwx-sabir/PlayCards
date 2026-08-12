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

        /// <summary>The player's full BoZo avatar config (structured AvatarDto serialized to JSON), server-synced and
        /// SANITIZED on write — distinct from the simple cosmetic <see cref="AvatarId"/>. Others load this to render the
        /// player's 3D avatar at their seat.</summary>
        [Column(TypeName = "text")]
        public string AvatarConfig { get; set; }

        // ---- Free-text blurbs (user-editable; moderated on write — see ProfileService) ----
        [MaxLength(160)]
        public string Bio { get; set; }            // "about me"

        [MaxLength(80)]
        public string StatusMessage { get; set; }  // short status line

        // ---- Region (denormalized so board writes/reads never join ApplicationUser) ----
        /// <summary>ISO-3166 alpha-2, UPPER. "ZZ" = unknown.</summary>
        [Required, MaxLength(2), Column(TypeName = "char(2)")]
        public string Region { get; set; } = "ZZ";

        // ---- Progression ----
        [Required] public int  Level { get; set; } = 1;
        [Required] public long Experience { get; set; } = 0;          // INTO-LEVEL XP (0..XpToNext); reset-with-carry on level-up by ProgressionService
        [Required] public long LifetimeExperience { get; set; } = 0;  // monotonic; safe XP-board source
        [Required] public long DailyXp { get; set; } = 0;             // XP earned today (drives the daily cap)
        public DateTime? DailyXpResetAt { get; set; }                 // UTC midnight after which DailyXp resets to 0

        // ---- VIP / loyalty ----
        [Required] public VipTier VipTier { get; set; } = VipTier.None;
        [Required] public long LoyaltyPoints { get; set; } = 0;       // current spendable balance
        [Required] public long LifetimeLoyaltyPoints { get; set; } = 0;

        // ---- VIP / status (Progression Spec §3) — tier itself is computed from StatusPointsLedger (trailing window) ----
        [Required] public long LifetimeStatusPoints { get; set; } = 0;  // monotonic SP, display/analytics (rank = trailing-window sum, not this)
        [Required] public long DailySpFromWager { get; set; } = 0;      // SP earned from wager today (drives SP_FROM_WAGER_DAILY_CAP)
        public DateTime? DailySpResetAt { get; set; }                   // UTC midnight after which DailySpFromWager resets
        public DateTime? BadgeLitUntil { get; set; }                    // badge shown lit until this UTC time (refreshed within BADGE_WINDOW_DAYS); COSMETIC — never affects tier
        [Required] public bool HideVipBadge { get; set; } = false;      // player opt-out: hide the VIP badge from others

        // ---- VIP Level (Progression Spec §3.6) — the premium ladder (1..10) on TOP of the tier ----
        [Required] public int VipLevel { get; set; } = 0;               // 0 = none; 1..10 the premium ladder (the big comp multiplier)
        [Required] public long VipLevelProgress { get; set; } = 0;      // into-next-level grind progress (SP × tier factor)
        public DateTime? VipLevelMaintainedThrough { get; set; }        // level holds until this UTC time; else the monthly review drops it 1 (floor 0)

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

        /// <summary>
        /// The player's IANA timezone ("Asia/Dhaka"), reported by the client at login and validated server-side.
        /// Null ⇒ UTC. Daily systems (the pass) roll over at the player's LOCAL midnight rather than UTC's, so a
        /// player's day doesn't end at breakfast. See Services/Pass/PassClock.cs.
        /// </summary>
        [MaxLength(64)]
        public string TimeZoneId { get; set; }

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
