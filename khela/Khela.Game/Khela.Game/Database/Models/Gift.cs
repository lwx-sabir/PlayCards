using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Social;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// A gift of in-game currency. Credited to the recipient's wallet on CLAIM via WalletService
    /// (TransactionType.Bonus, idempotent on CorrelationId — a Bonus grant is never a wager). Today the
    /// only producer is the daily "send free chips" loop, which hardcodes <see cref="CurrencyType.Chips"/>
    /// (GiftService.SendAsync); the <see cref="Currency"/> field leaves room for future paid Kash/cosmetic
    /// gifts. The tradeable token is NEVER giftable. The daily send limit is enforced in the service
    /// (a Redis counter), not by the schema.
    /// </summary>
    [Table("Gifts")]
    [Index(nameof(RecipientId), nameof(Status))]    // a user's claimable gifts
    [Index(nameof(SenderId), nameof(SentAt))]       // sent history + daily-limit count
    [Index(nameof(CorrelationId), IsUnique = true)] // idempotent claim credit
    public class Gift
    {
        [Key]
        public Guid GiftId { get; set; } = Guid.NewGuid();

        [Required] public Guid SenderId { get; set; }
        [Required] public Guid RecipientId { get; set; }

        [Required] public CurrencyType Currency { get; set; } = CurrencyType.Chips;

        [Precision(18, 4)]
        public decimal Amount { get; set; }

        [Required] public GiftStatus Status { get; set; } = GiftStatus.Sent;

        [MaxLength(140)]
        public string Message { get; set; }

        [Required] public DateTime SentAt { get; set; } = DateTime.UtcNow;
        public DateTime? ClaimedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }

        /// <summary>Wallet-credit idempotency key applied when the gift is claimed.</summary>
        [Required, MaxLength(80)]
        public string CorrelationId { get; set; }

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
