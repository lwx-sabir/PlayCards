using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Leaderboards;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// Per-player, per-game, per-UTC-day stat bucket — the source for WINDOWED leaderboards/stats
    /// (daily / weekly / monthly = a date-range query over these rows). One row per (UserId, GameType, StatDate),
    /// upserted on every settled round next to <see cref="UserGameStats"/>. Additive metrics accumulate (+=);
    /// <see cref="BiggestSingleWin"/> keeps the day's max. NO streak column — a streak can cross day boundaries
    /// and can't be reconstructed from daily buckets, so streak boards stay AllTime (UserGameStats).
    ///
    /// AllTime is NOT derived from these (it lives in UserGameStats per-game + UserProfile cross-game); these are
    /// pruned to a configured retention window. Cross-game windowed = the same rows without a GameType filter.
    /// Rebuildable, non-money — a write failure here never affects balances.
    /// </summary>
    [Table("PlayerDailyStats")]
    [Index(nameof(UserId), nameof(GameType), nameof(StatDate), IsUnique = true)] // natural key + upsert
    [Index(nameof(GameType), nameof(StatDate))]                                  // per-game windowed top-N scan
    [Index(nameof(StatDate))]                                                    // cross-game scan + nightly prune
    public class PlayerDailyStat
    {
        [Key]
        public Guid PlayerDailyStatId { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }
        [Required] public Khela.Common.Leaderboards.GameType GameType { get; set; }

        /// <summary>The UTC day this bucket covers (date only, no time).</summary>
        [Required, Column(TypeName = "date")] public DateTime StatDate { get; set; }

        /// <summary>Region snapshot so a Country-scope windowed query needs no join.</summary>
        [Required, MaxLength(2), Column(TypeName = "char(2)")] public string Region { get; set; } = "ZZ";

        // ---- additive (summed over the range) ----
        [Required] public long Xp { get; set; } = 0;
        [Required] public long GamesPlayed { get; set; } = 0;
        [Required] public long GamesWon { get; set; } = 0;

        [Precision(28, 4)] public decimal Wagered { get; set; } = 0m;   // matches UserGameStats.TotalWagered semantics
        [Precision(28, 4)] public decimal ChipsWon { get; set; } = 0m;  // gross winnings
        [Precision(28, 4)] public decimal NetProfit { get; set; } = 0m; // signed

        // ---- daily max (MAX of these over a range = the window's biggest win) ----
        [Precision(18, 4)] public decimal BiggestSingleWin { get; set; } = 0m;

        [Required] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
