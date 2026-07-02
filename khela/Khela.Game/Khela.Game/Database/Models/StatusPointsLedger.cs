using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// Monthly Status-Point + spend bucket for the VIP system (Progression Spec §3). One row per
    /// (UserId, PeriodStart-month). A player's VIP TIER is the band their SUM over the trailing
    /// <c>TIER_WINDOW_MONTHS</c> buckets qualifies for (plus the band's spend floor) — so the rolling
    /// window's natural roll-off IS the gentle decay; there is no separate step-down ledger.
    ///
    /// <see cref="Sp"/> is stored at a FLAT ×1 rate (the tier multiplier is a benefit on the Loyalty
    /// track, never applied to status). <see cref="SpendUsd"/> is real-money spend that month (IAP now;
    /// the later web store feeds the same field), used only for the upper-band spend floors. SP NEVER
    /// accrues from winnings.
    /// </summary>
    [Table("StatusPointsLedger")]
    [Index(nameof(UserId), nameof(PeriodStart), IsUnique = true)] // natural key + upsert + trailing-window scan (UserId-prefix)
    public class StatusPointsLedger
    {
        [Key]
        public Guid StatusPointsLedgerId { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }

        /// <summary>First day (UTC) of the month this bucket covers — the monthly key for the trailing-window sum.</summary>
        [Required, Column(TypeName = "date")]
        public DateTime PeriodStart { get; set; }

        /// <summary>Total Status Points earned this month, FLAT ×1 (wager + activity + purchase). Tier = trailing sum.</summary>
        [Required] public long Sp { get; set; } = 0;

        /// <summary>Real-money spend this month (USD), for the upper-band spend floors. 0 until IAP feeds it.</summary>
        [Precision(18, 4)] public decimal SpendUsd { get; set; } = 0m;

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [Required] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
