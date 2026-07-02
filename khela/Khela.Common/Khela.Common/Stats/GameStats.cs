using System.Collections.Generic;
using Khela.Common.Leaderboards;

namespace Khela.Common.Stats
{
    /// <summary>
    /// One named lifetime stat counter for a game (key + display label + value). The server fills the per-game
    /// list from <see cref="GameStatCatalog"/> so the client just renders it — no per-stat client code.
    /// </summary>
    public sealed class StatCounterDto
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public long Value { get; set; }
    }

    /// <summary>
    /// Stable counter KEY strings — the keys of the JSON counter bag on UserGameStats. The settle-time delta
    /// computation and the display catalog MUST agree on these. Game-namespaced by convention isn't needed since
    /// the bag is per-(user, game), but keep keys unique within a game.
    /// </summary>
    public static class GameStatKeys
    {
        // ---- Blackjack ----
        public const string HandsPlayed    = "handsPlayed";
        public const string HandsWon       = "handsWon";
        public const string HandsLost      = "handsLost";
        public const string Pushes         = "pushes";
        public const string Blackjacks     = "blackjacks";
        public const string Busts          = "busts";
        public const string Doubles        = "doubles";
        public const string Pairs          = "pairs";    // opening hand was dealt a splittable pair
        public const string Splits         = "splits";
        public const string InsuranceTaken = "insuranceTaken";
        public const string InsuranceWon   = "insuranceWon";
    }

    /// <summary>
    /// The per-game ORDERED list of lifetime stat counters to display (key → label). Adding a stat is just a new
    /// entry here + emitting the same key at settle — NO schema change (values live in a JSON bag on
    /// UserGameStats). Each game declares its own "playstyle" counters; an undeclared game shows none.
    /// </summary>
    public static class GameStatCatalog
    {
        private static readonly IReadOnlyList<(string Key, string Label)> BlackjackCounters = new[]
        {
            (GameStatKeys.HandsPlayed,    "Hands Played"),
            (GameStatKeys.HandsWon,       "Hands Won"),
            (GameStatKeys.HandsLost,      "Hands Lost"),
            (GameStatKeys.Pushes,         "Pushes"),
            (GameStatKeys.Blackjacks,     "Blackjacks"),
            (GameStatKeys.Busts,          "Busts"),
            (GameStatKeys.Doubles,        "Doubles"),
            (GameStatKeys.Pairs,          "Pairs Dealt"),
            (GameStatKeys.Splits,         "Splits"),
            (GameStatKeys.InsuranceTaken, "Insurance Bets"),
            (GameStatKeys.InsuranceWon,   "Insurance Wins"),
        };

        private static readonly IReadOnlyList<(string Key, string Label)> None = new (string, string)[0];

        /// <summary>The ordered (key, label) counters for a game's stats panel (empty if the game declares none yet).</summary>
        public static IReadOnlyList<(string Key, string Label)> For(GameType game) => game switch
        {
            GameType.Blackjack => BlackjackCounters,
            _ => None,
        };
    }
}
