using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    public enum TransactionStatus
    {
        Pending,
        Completed,
        Failed,
        Reversed
    }

    public enum TransactionType
    {
        Bet,
        Win,
        Purchase,
        Refund,
        Bonus,
        AdminAdjustment
    }

    [Table("WalletTransactions")]
    [Index(nameof(WalletId), nameof(CorrelationId), IsUnique = true)]
    [Index(nameof(WalletId), nameof(CreatedAt))]
    public class WalletTransaction
    {
        [Key]
        public Guid TransactionId { get; set; } = Guid.NewGuid();

        [Required]
        public Guid WalletId { get; set; }  // FK to PlayerWallets

        [Precision(18, 4)]
        [Required]
        public decimal Amount { get; set; }

        // Tainted slice of this movement (signed; the earned slice is Amount - GiftedDelta). Lets progression
        // read exactly how much of a bet was drawn from EARNED chips. Defaults 0 = fully earned; historical
        // rows are 0 (treated as earned).
        [Precision(18, 4)]
        [Required]
        public decimal GiftedDelta { get; set; } = 0m;

        [Required]
        public TransactionType Type { get; set; }

        [Required]
        public TransactionStatus Status { get; set; } = TransactionStatus.Pending;

        public Guid? GameId { get; set; }  // Optional: which game caused this transaction

        [MaxLength(500)]
        public string Description { get; set; }  // Optional notes, e.g., "Poker win round 5"

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        public DateTime? CompletedAt { get; set; }

        public DateTime? FailedAt { get; set; }

        public DateTime? ReversedAt { get; set; }

        [MaxLength(64)]
        public string CorrelationId { get; set; }  // idempotency key per wallet

        [MaxLength(128)]
        public string ExternalRef { get; set; }  // payment provider or game hand id

        [MaxLength(128)]
        public string TableId { get; set; }

        [MaxLength(128)]
        public string RoundId { get; set; }

        [Precision(18, 4)]
        public decimal? BalanceBefore { get; set; }

        [Precision(18, 4)]
        public decimal? BalanceAfter { get; set; }

        // Per-row snapshot of the tainted slice (mirrors BalanceBefore/After) for dispute/audit — so a settle
        // dispute can read the gifted balance at the moment of the movement without summing GiftedDelta rows.
        [Precision(18, 4)]
        public decimal? GiftedBalanceBefore { get; set; }

        [Precision(18, 4)]
        public decimal? GiftedBalanceAfter { get; set; }

        public string MetadataJson { get; set; }
    }
}
