using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Leaderboards;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// A concrete, time-bounded window of a <see cref="LeaderboardDefinition"/>, identified by
    /// (DefinitionId, PeriodKey, RegionKey). Created lazily on first write (idempotent upsert; the
    /// unique index makes the race safe). The Redis ZSET key derives from this row:
    /// "lb:{DefinitionId}:{PeriodKey}:{RegionKey}". Window boundaries are frozen at creation.
    /// </summary>
    [Table("LeaderboardInstances")]
    [Index(nameof(DefinitionId), nameof(PeriodKey), nameof(RegionKey), IsUnique = true)] // natural key + race-safe lazy create
    [Index(nameof(Status), nameof(ClosesAt))]  // scheduler scan: open & due to seal
    public class LeaderboardInstance
    {
        [Key]
        public Guid InstanceId { get; set; } = Guid.NewGuid();

        /// <summary>Loose ref to LeaderboardDefinition. No EF navigation.</summary>
        [Required]
        public Guid DefinitionId { get; set; }

        /// <summary>Window instance id: "ALL","2026-W24","2026-06","2026-06-16","S7" (season).</summary>
        [Required, MaxLength(48)]
        public string PeriodKey { get; set; }

        /// <summary>"GLOBAL" or ISO alpha-2 region. MaxLength(8) gives headroom for future grouping.</summary>
        [Required, MaxLength(8)]
        public string RegionKey { get; set; } = "GLOBAL";

        /// <summary>Denormalized from the definition for scheduler filtering without a join.</summary>
        [Required] public Khela.Common.Leaderboards.GameType GameType { get; set; }

        [Required] public DateTime OpensAt  { get; set; }
        public DateTime? ClosesAt { get; set; } // null for the eternal AllTime instance

        [Required] public LeaderboardInstanceStatus Status { get; set; } = LeaderboardInstanceStatus.Open;
        public DateTime? SealedAt { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }

    /// <summary>Scheduler state machine. Server-only — the client never sees it.</summary>
    public enum LeaderboardInstanceStatus
    {
        Open     = 0,
        Sealing  = 1,
        Sealed   = 2,
        Archived = 3
    }
}
