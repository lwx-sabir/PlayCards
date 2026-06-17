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
    }
}
