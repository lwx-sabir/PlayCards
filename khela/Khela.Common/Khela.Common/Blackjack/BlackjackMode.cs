namespace Khela.Common.Blackjack
{
    /// <summary>
    /// Blackjack table rule variant, surfaced as the lobby's mode tabs. Classic is implemented;
    /// the others are metadata-only for now (they show in the lobby) until their rule sets are built.
    /// </summary>
    public enum BlackjackMode
    {
        Classic = 0,
        HiLo = 1,
        BustOut = 2,
        LuckyQueens = 3
    }
}
