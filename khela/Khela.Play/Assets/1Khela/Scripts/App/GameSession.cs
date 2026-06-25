namespace PlayCard.App
{
    /// <summary>
    /// Cross-scene state handed between screens — e.g. which table the lobby chose to open.
    /// Static so it survives scene loads within a play session; cleared on app restart.
    /// </summary>
    public static class GameSession
    {
        /// <summary>Table id the Table scene should open (set by the lobby before loading it).</summary>
        public static string TableId { get; set; }

        /// <summary>Game chosen on the Home carousel (e.g. "Blackjack") — the lobby + auto-match filter by it.</summary>
        public static string SelectedGame { get; set; }

        /// <summary>Seat (1-based) the player picked in the lobby, so the Table scene resolves the local seat
        /// INSTANTLY (camera + Leave) without waiting for the first board snapshot. 0 = unknown (auto-match or
        /// spectate) → fall back to the board. Set by <see cref="SceneNavigator.GoToTable"/>; the board still wins
        /// once it arrives and matches us.</summary>
        public static int SeatNumber { get; set; }
    }
}
