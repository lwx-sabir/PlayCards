using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// One Loyalty-Store redemption (Progression Spec §4) — the idempotency + audit anchor so a redeem can't
    /// double-spend. Keyed by the client <see cref="IdempotencyKey"/> (unique). The two step flags make recovery
    /// exact: a retry re-drives only the incomplete step — the LP deduction is guarded by <see cref="LpDeducted"/>,
    /// and the chip credit is idempotent on its wallet CorrelationId — so a crash mid-redeem heals on the next attempt.
    /// </summary>
    [Table("LoyaltyRedemptions")]
    [Index(nameof(IdempotencyKey), IsUnique = true)]
    [Index(nameof(UserId), nameof(CreatedAt))]
    public class LoyaltyRedemption
    {
        [Key] public Guid LoyaltyRedemptionId { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }
        [Required, MaxLength(80)] public string IdempotencyKey { get; set; }
        [Required, MaxLength(64)] public string ItemId { get; set; }
        [Required, MaxLength(16)] public string Kind { get; set; } = "chips";
        [Required] public long CostLp { get; set; }
        [Precision(18, 4)] public decimal ChipAmount { get; set; }

        [Required] public bool LpDeducted { get; set; } = false;
        [Required] public bool ChipsCredited { get; set; } = false;
        [Required, MaxLength(16)] public string Status { get; set; } = "Pending";   // Pending | Completed | Failed

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
