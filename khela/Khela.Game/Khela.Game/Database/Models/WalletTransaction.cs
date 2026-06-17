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

        public string MetadataJson { get; set; }
    }
}
