using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// Immutable snapshot of the top-N of a SEALED <see cref="LeaderboardInstance"/> — drives reward
    /// payout and history. Written once at seal time (rank = index+1). Append-only; only
    /// RewardGranted/RewardCorrelationId flip later (guarded by WalletService idempotency).
    /// bigint identity PK for InnoDB insert locality (the one high-volume, append-only table).
    /// </summary>
    [Table("LeaderboardArchiveEntries")]
    [Index(nameof(InstanceId), nameof(Rank), IsUnique = true)] // final board in rank order
    [Index(nameof(UserId), nameof(InstanceId))]                // "my past placements"
    [Index(nameof(RewardGranted), nameof(InstanceId))]         // payout-worker sweep
    public class LeaderboardArchiveEntry
    {
        /// <summary>bigint identity — high-volume append-only table; clustered-index locality (intentional deviation from the Guid convention).</summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long ArchiveEntryId { get; set; }

        [Required] public Guid InstanceId   { get; set; } // loose ref to LeaderboardInstance
        [Required] public Guid DefinitionId { get; set; } // denormalized for cross-instance queries

        [Required] public Guid UserId { get; set; }

        // Denormalized display payload snapshotted at seal (no join to render a board page):
        [Required, MaxLength(32)] public string DisplayName { get; set; }
        [MaxLength(256)] public string AvatarId { get; set; }
        [MaxLength(128)] public string AvatarFrameId { get; set; }

        [Required, MaxLength(8), Column(TypeName = "varchar(8)")]
        public string RegionKey { get; set; }

        [Required] public int Rank { get; set; } // 1-based final placement

        /// <summary>Final score. Precision(28,4) holds large cumulative chip totals without overflow.</summary>
        [Precision(28, 4)]
        public decimal Score { get; set; }

        // ---- Reward payout (idempotent via WalletService CorrelationId) ----
        [Required] public bool RewardGranted { get; set; } = false;
        [MaxLength(128)] public string RewardCorrelationId { get; set; }
        public DateTime? RewardGrantedAt { get; set; }

        // Frozen window bounds for audit / payout windows:
        [Required] public DateTime PeriodStartUtc { get; set; }
        [Required] public DateTime PeriodEndUtc   { get; set; }
        [Required] public DateTime SealedAt       { get; set; } = DateTime.UtcNow;

        // No RowVersion: write-once. The RewardGranted flip is guarded by RewardCorrelationId idempotency.
    }
}
