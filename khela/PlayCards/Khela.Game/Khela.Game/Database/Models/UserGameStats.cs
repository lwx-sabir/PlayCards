using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Leaderboards;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// Per-game lifetime stats. One row per (UserId, GameType). The authoritative source the AllTime
    /// leaderboards rebuild/fallback from, and the profile's per-game breakdown. Windowed boards are
    /// NOT derived from these — they accumulate per-window deltas in Redis.
    /// </summary>
    [Table("UserGameStats")]
    [Index(nameof(UserId), nameof(GameType), IsUnique = true)] // natural key + upsert + per-user fetch
    [Index(nameof(GameType), nameof(NetProfit))]               // AllTime NetProfit fallback top-N
    [Index(nameof(GameType), nameof(ChipsWon))]                // AllTime ChipsWon fallback top-N
    public class UserGameStats
    {
        [Key]
        public Guid UserGameStatsId { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }
        [Required] public Khela.Common.Leaderboards.GameType GameType { get; set; }

        /// <summary>Region snapshot so an AllTime regional rebuild needs no join.</summary>
        [Required, MaxLength(2), Column(TypeName = "char(2)")]
        public string Region { get; set; } = "ZZ";

        [Required] public long GamesPlayed  { get; set; } = 0;
        [Required] public long GamesWon     { get; set; } = 0;
        [Required] public long RoundsPlayed { get; set; } = 0;
        [Required] public long RoundsWon    { get; set; } = 0;

        // 28,4 (not 18,4): lifetime monotonic accumulators — a whale can exceed ~1e14 chips over years.
        // Matches LeaderboardArchiveEntry.Score, which these feed. BiggestSingleWin stays 18,4 (a single
        // win, not a sum, so it can't accumulate past the cap).
        [Precision(28, 4)] public decimal ChipsWon         { get; set; } = 0m; // gross, monotonic
        [Precision(28, 4)] public decimal TotalWagered     { get; set; } = 0m; // monotonic
        [Precision(28, 4)] public decimal NetProfit        { get; set; } = 0m; // signed
        [Precision(18, 4)] public decimal BiggestSingleWin { get; set; } = 0m; // MAX (single win)

        [Required] public int CurrentWinStreak { get; set; } = 0;
        [Required] public int LongestWinStreak { get; set; } = 0;

        [Required] public long ExperienceEarned { get; set; } = 0; // per-game lifetime XP

        // ---- Per-game preference + recency (favourites + "jump back in"; most-played = GamesPlayed) ----
        [Required] public bool IsFavorite { get; set; } = false;
        public DateTime? FirstPlayedAt { get; set; }
        public DateTime? LastPlayedAt  { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [Required] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
