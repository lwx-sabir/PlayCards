using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    [Table("GameHandParticipants")]
    [Index(nameof(HandId))]
    [Index(nameof(UserId))]
    public class GameHandParticipant
    {
        [Key]
        public Guid ParticipantId { get; set; } = Guid.NewGuid();

        [Required]
        public Guid HandId { get; set; }

        [Required]
        public Guid UserId { get; set; }

        public int SeatNumber { get; set; }

        /// <summary>
        /// Which of the player's hands this row records. 0 for a normal hand; a split produces one row per
        /// hand (0, 1, …) so each split hand's stake, outcome, and payout are audited independently.
        /// </summary>
        public int HandIndex { get; set; }

        [Precision(18, 4)]
        public decimal Bet { get; set; }

        [Precision(18, 4)]
        public decimal InsuranceBet { get; set; }

        [Precision(18, 4)]
        public decimal Payout { get; set; }

        public int FinalHandValue { get; set; }

        public bool Bust { get; set; }

        public bool Blackjack { get; set; }

        public string Outcome { get; set; } // win/lose/push etc.

        [MaxLength(128)]
        public string WalletDebitTxId { get; set; }

        [MaxLength(128)]
        public string WalletCreditTxId { get; set; }

        [Precision(18, 4)]
        public decimal? BalanceBefore { get; set; }

        [Precision(18, 4)]
        public decimal? BalanceAfter { get; set; }

        public string MetadataJson { get; set; }

        /// <summary>
        /// True once the reconciliation sweeper (Part B) has healed a stranded row — a <c>settle_failed</c>
        /// (credit re-applied / stake refunded) entry. Normal settled rows stay false; the sweeper scans for
        /// unresolved problem rows. <c>settle_mismatch</c> rows are left for human/ops review (already paid).
        /// </summary>
        public bool Resolved { get; set; }
    }
}
