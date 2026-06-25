namespace Khela.Common.Reports
{
    /// <summary>What a report targets. Persisted as int + sent to clients — append-only.</summary>
    public enum ReportTargetType
    {
        Player  = 0,
        Message = 1
    }

    /// <summary>Why a player/message was reported. Persisted as int + sent to clients — append-only.</summary>
    public enum ReportReason
    {
        Harassment   = 0,
        HateSpeech   = 1,
        Sexual       = 2,
        Spam         = 3,
        Solicitation = 4,
        ContactInfo  = 5,
        Cheating     = 6,
        Other        = 7
    }

    /// <summary>Moderation lifecycle of a report. Persisted as int — append-only.</summary>
    public enum ReportStatus
    {
        Open        = 0,
        Reviewing   = 1,
        ActionTaken = 2,
        Dismissed   = 3
    }

    /// <summary>How the report was raised. Persisted as int — append-only.</summary>
    public enum ReportSource
    {
        User     = 0,
        AutoFlag = 1
    }
}
