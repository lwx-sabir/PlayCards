using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Reports;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// A player- or message-report for moderation review. Loose Guid user ids, no EF navigation (PlayerWallet
    /// convention). For message reports the offending content is frozen into <see cref="ContextSnapshot"/> at
    /// report time, because room/global chat is Redis-ephemeral and the original would otherwise be gone by the
    /// time an admin looks. Reports are append-only by users; only admins mutate <see cref="Status"/>.
    /// </summary>
    [Table("Reports")]
    [Index(nameof(ReportedUserId), nameof(Status))]   // "open reports against player X"
    [Index(nameof(Status), nameof(CreatedAt))]        // admin queue, oldest-first
    public class Report
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid ReporterUserId { get; set; }
        [Required] public Guid ReportedUserId { get; set; }

        [Required] public ReportTargetType TargetType { get; set; }

        /// <summary>The reported DM's MessageId when <see cref="TargetType"/> is Message and it was persisted.</summary>
        public Guid? TargetMessageId { get; set; }

        /// <summary>JSON snapshot of the offending message(s) + a little surrounding context, captured at report
        /// time (required for message reports — ephemeral chat can't be re-fetched later).</summary>
        [Column(TypeName = "longtext")]
        public string ContextSnapshot { get; set; }

        [Required] public ReportReason Reason { get; set; }

        [MaxLength(500)]
        public string Details { get; set; }

        [Required] public ReportStatus Status { get; set; } = ReportStatus.Open;
        [Required] public ReportSource Source { get; set; } = ReportSource.User;

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ResolvedAt { get; set; }
        public Guid? ResolvedByAdminId { get; set; }

        [MaxLength(1000)]
        public string ActionNote { get; set; }

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
