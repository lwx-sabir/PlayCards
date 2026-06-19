using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Home
{
    /// <summary>
    /// Master registry of every planned game (carousel order). Holds games that aren't built yet too, so the
    /// roadmap lives in config: only entries with <see cref="GameDefinition.available"/> are playable, the
    /// rest are coming-soon. A single asset; reference it from menus / a future "all games" screen, and
    /// optionally to build the carousel from config instead of hand-placed tables.
    /// </summary>
    [CreateAssetMenu(menuName = "Khela/Game Catalog", fileName = "GameCatalog")]
    public sealed class GameCatalog : ScriptableObject
    {
        public List<GameDefinition> games = new List<GameDefinition>();

        /// <summary>Find a game by its routing key (case-insensitive); null if not in the catalog.</summary>
        public GameDefinition Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var g in games)
                if (g != null && string.Equals(g.key, key, System.StringComparison.OrdinalIgnoreCase))
                    return g;
            return null;
        }
    }
}
