namespace Khela.Common.Social
{
    /// <summary>Friend-graph edge state. Persisted as int + sent to the client — append-only.</summary>
    public enum FriendshipStatus
    {
        Pending  = 0,
        Accepted = 1,
        Declined = 2,
        Blocked  = 3
    }

    /// <summary>Lifecycle of a sent gift (e.g. free chips).</summary>
    public enum GiftStatus
    {
        Sent      = 0,
        Claimed   = 1,
        Expired   = 2,
        Cancelled = 3
    }

    /// <summary>Moderation state of a chat message — the future AI moderator sets this.</summary>
    public enum MessageModerationStatus
    {
        Pending  = 0,
        Approved = 1,
        Flagged  = 2,
        Removed  = 3
    }

    /// <summary>Chat channel kind. DM persists to MySQL; Table/Global are Redis-ephemeral.</summary>
    public enum ChatChannelType
    {
        Dm     = 0,
        Table  = 1,
        Global = 2
    }
}
