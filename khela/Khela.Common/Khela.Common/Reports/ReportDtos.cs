using System;

namespace Khela.Common.Reports
{
    /// <summary>Report a specific message (DM or room) — the offending content must be snapshotted client-side.</summary>
    public class ReportMessageRequest
    {
        public string ReportedUserId { get; set; } = string.Empty;
        /// <summary>MessageId of a persisted DM, if applicable (room/global messages are ephemeral → null).</summary>
        public string TargetMessageId { get; set; }
        /// <summary>JSON of the offending message(s) + a little context. Required — chat may be gone by review time.</summary>
        public string ContextSnapshot { get; set; } = string.Empty;
        public ReportReason Reason { get; set; }
        public string Details { get; set; }
    }

    /// <summary>Report a player (not tied to a single message).</summary>
    public class ReportPlayerRequest
    {
        public string ReportedUserId { get; set; } = string.Empty;
        public ReportReason Reason { get; set; }
        public string Details { get; set; }
    }

    /// <summary>Admin resolution of a report. Action must be a terminal/Reviewing status.</summary>
    public class ResolveReportRequest
    {
        public ReportStatus Action { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Admin-facing view of a report row.</summary>
    public class ReportDto
    {
        public string Id { get; set; } = string.Empty;
        public string ReporterUserId { get; set; } = string.Empty;
        public string ReportedUserId { get; set; } = string.Empty;
        public ReportTargetType TargetType { get; set; }
        public string TargetMessageId { get; set; }
        public string ContextSnapshot { get; set; }
        public ReportReason Reason { get; set; }
        public string Details { get; set; }
        public ReportStatus Status { get; set; }
        public ReportSource Source { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string ResolvedByAdminId { get; set; }
        public string ActionNote { get; set; }
    }
}
