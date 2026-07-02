using UnityEngine;

namespace PlayCard.Home
{
    /// <summary>Broad grouping for lobby/branding; not the game id (that's <see cref="GameDefinition.key"/>).</summary>
    public enum GameCategory { Cards, Table, Slots, Bingo, Sports }

    /// <summary>
    /// Config for one game in the catalog (Blackjack, Teen Patti, Roulette, Slots, …). One ScriptableObject
    /// asset per game — adding a game is a config asset, not a code change. The 3D table in the carousel
    /// references this; the carousel/HUD read the title + availability + routing key from it.
    /// </summary>
    [CreateAssetMenu(menuName = "Khela/Game Definition", fileName = "Game_")]
    public sealed class GameDefinition : ScriptableObject
    {
        [Tooltip("Stable id for routing + the lobby endpoint, e.g. \"blackjack\", \"teenpatti\". lowercase, no spaces.")]
        public string key = "blackjack";

        [Tooltip("Shown in the HUD title, e.g. \"BLACKJACK\".")]
        public string displayName = "BLACKJACK";

        [Tooltip("Server leaderboard GameType id, used to match this game's profile stats (Blackjack=1, Poker=2, " +
                 "TeenPatti=3, Roulette=4). 0 = not tracked on the server yet → the per-game stats tab shows zeros. " +
                 "This is the explicit client↔server map — the two GameType enums diverge, so never cast between them.")]
        public int leaderboardGameId = 0;

        public GameCategory category = GameCategory.Cards;

        [Tooltip("Is the game actually built (engine + lobby on the server)? OFF = appears in the carousel as a " +
                 "coming-soon placeholder; Play Now / Lobby are disabled.")]
        public bool available = false;

        [Tooltip("Badge text shown when not available.")]
        public string comingSoonLabel = "COMING SOON";

        [Header("Branding (optional)")]
        public Sprite icon;
        public Color accent = Color.white;
    }
}
