using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Leaderboards;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// Declares a leaderboard family: (GameType, Metric, Period, Scope) + operational knobs. A new
    /// board is one INSERT, never a migration. A few dozen rows total — cache in memory. This is what
    /// avoids a table-per-(game x metric x period x scope) explosion.
    /// </summary>
    [Table("LeaderboardDefinitions")]
    [Index(nameof(Code), IsUnique = true)]
    [Index(nameof(GameType), nameof(Metric), nameof(Period), nameof(Scope), IsUnique = true)]
    [Index(nameof(IsActive))]
    public class LeaderboardDefinition
    {
        [Key]
        public Guid DefinitionId { get; set; } = Guid.NewGuid();

        /// <summary>Stable slug used in Redis keys + client URLs, e.g. "bj_chipswon_weekly_regional".</summary>
        [Required, MaxLength(64)]
        public string Code { get; set; }

        [Required, MaxLength(64)]
        public string DisplayName { get; set; }

        [Required] public Khela.Common.Leaderboards.GameType GameType    { get; set; }
        [Required] public LeaderboardMetric Metric      { get; set; }
        [Required] public MetricAggregation Aggregation { get; set; } // Sum or Max
        [Required] public LeaderboardPeriod Period      { get; set; }
        [Required] public LeaderboardScope  Scope       { get; set; }

        // ---- Operational knobs (all config; tune without a schema change) ----
        [Required] public bool IsActive { get; set; } = true;

        /// <summary>Higher score = better when true (almost always). Lets a metric rank ascending if ever needed.</summary>
        [Required] public bool HigherIsBetter { get; set; } = true;

        /// <summary>Top-N to persist to the archive on seal (e.g. 1000). Redis may hold more for live scroll.</summary>
        [Required] public int SnapshotTopN { get; set; } = 1000;

        /// <summary>Redis TTL (hours) for CLOSED instances before eviction. 0 = no TTL.</summary>
        [Required] public int RedisRetentionHours { get; set; } = 0;

        /// <summary>If true, sealed periods produce archive rows + reward payout. Ephemeral (e.g. Daily) boards may set false.</summary>
        [Required] public bool Archivable { get; set; } = true;

        /// <summary>Season length in days when Period == Season; ignored otherwise.</summary>
        public int? SeasonLengthDays { get; set; }

        /// <summary>Optional reward-catalog id for winners. Loose ref, no FK.</summary>
        public Guid? RewardTableId { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [Required] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
