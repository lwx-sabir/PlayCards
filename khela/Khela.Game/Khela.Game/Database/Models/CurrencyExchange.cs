using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// One currency exchange (A → B at the catalog rate): the audit row beside the two wallet movements it made. The
    /// wallet ledger is the money truth (both legs carry this row's id in their correlation ids, <c>xchg:{Id:N}:d</c> /
    /// <c>:c</c>); this row is what the per-player caps are counted from and what the admin ledger lists. Idempotent on
    /// (UserId, RequestId): a retry with the same request id replays the original outcome and moves nothing.
    /// </summary>
    [Table("CurrencyExchanges")]
    [Index(nameof(UserId), nameof(RequestId), IsUnique = true)]
    [Index(nameof(UserId), nameof(PairKey), nameof(CreatedAt))]
    [Index(nameof(CreatedAt))]
    public class CurrencyExchange
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }

        /// <summary>The client's idempotency key for this tap.</summary>
        [Required] public Guid RequestId { get; set; }

        [Required, MaxLength(32)] public string PairKey { get; set; }

        [Required] public CurrencyType FromCurrency { get; set; }
        [Required, Column(TypeName = "decimal(18,4)")] public decimal FromAmount { get; set; }

        [Required] public CurrencyType ToCurrency { get; set; }
        [Required, Column(TypeName = "decimal(18,4)")] public decimal ToAmount { get; set; }

        /// <summary>
        /// The rate applied: units of FROM per one unit of TO, as the catalog said at the time. Wider than the wallet's scale
        /// because a rate is allowed to be finer than a wallet unit (0.000002 Kash per chip, with a step that makes the COST
        /// exact) — informational: <see cref="FromAmount"/> / <see cref="ToAmount"/> are the money truth.
        /// </summary>
        [Required, Column(TypeName = "decimal(28,10)")] public decimal RateFromPerUnit { get; set; }

        /// <summary>Pending → Completed | Failed.</summary>
        [Required, MaxLength(16)] public string Status { get; set; } = "Pending";
        [MaxLength(200)] public string Error { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }
}
