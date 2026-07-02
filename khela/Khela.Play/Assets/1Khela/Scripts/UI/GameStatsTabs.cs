using System.Collections.Generic;
using PlayCard.Game.Net;       // UserProfileData, ProfileStats, GameStatsDto
using PlayCard.Game.Profile;   // ProfileManager
using PlayCard.Home;           // GameCatalog, GameDefinition
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Controller for the profile game-stats tabs. Tabs = [ "All" = cross-game aggregate ] + one per game.
    ///
    /// The game list comes from the <see cref="games"/> list you assign on this component (drag in your
    /// GameDefinition assets) — or a <see cref="GameCatalog"/> if you'd rather use one shared registry. EVERY listed
    /// game shows a tab, including ones the player hasn't played yet and coming-soon ones (Slot, Three Card Poker, …);
    /// those simply read zeros. A game's numbers come from the server's per-game stats
    /// (<see cref="ProfileManager.PerGame"/>) matched by <see cref="GameDefinition.leaderboardGameId"/>; if there's
    /// no match (never played, or not tracked server-side yet) the tab shows an empty stat block (all zeros).
    /// Adding a game tab is therefore a config-asset change (add it to the catalog), not code.
    ///
    /// If no catalog is assigned it falls back to listing only games the player has actually played (perGame order).
    /// Repaints on <see cref="ProfileManager.OnProfileChanged"/>; selection survives a refresh (matched by label),
    /// defaulting to "All".
    /// </summary>
    public sealed class GameStatsTabs : MonoBehaviour
    {
        [Header("Refs")]
        [Tooltip("The ScrollRect Content (the object with the Horizontal Layout Group) the tabs are spawned into.")]
        [SerializeField] private Transform tabContainer;
        [Tooltip("Prefab with a GameStatsTabButton (label + optional highlight + Button).")]
        [SerializeField] private GameStatsTabButton tabPrefab;
        [Tooltip("The stat block that shows the selected tab's rows.")]
        [SerializeField] private StatBlockView statBlock;

        [Header("Games shown")]
        [Tooltip("The games to show as tabs, in order — drag your GameDefinition assets here. Every entry gets a " +
                 "tab (played or not); unplayed/coming-soon ones read zeros. Leave empty to fall back to only " +
                 "the games the player has played.")]
        [SerializeField] private List<GameDefinition> games = new List<GameDefinition>();
        [Tooltip("Optional: a GameCatalog whose 'games' list is used INSTEAD of the list above (when set + non-empty).")]
        [SerializeField] private GameCatalog catalog;
        [Tooltip("Show coming-soon / not-yet-built games too (they read zeros). Off = only built (available) games.")]
        [SerializeField] private bool includeComingSoon = true;

        [Header("Options")]
        [Tooltip("Label for the first (cross-game aggregate) tab.")]
        [SerializeField] private string allTabLabel = "All";
        [Tooltip("Pull a fresh profile from the server when this panel opens.")]
        [SerializeField] private bool refreshOnEnable = true;

        private readonly List<GameStatsTabButton> _tabs = new List<GameStatsTabButton>();
        private readonly List<string> _labels = new List<string>();
        private readonly List<StatBlockView.Stats> _stats = new List<StatBlockView.Stats>();
        private int _selected = -1;

        private void OnEnable()
        {
            var pm = ProfileManager.Instance;
            if (pm != null)
            {
                pm.OnProfileChanged += HandleProfileChanged;
                Rebuild();                       // paint cache now (empty until first load — handled)
                if (refreshOnEnable) RequestRefresh();
            }
            else Debug.LogWarning("[GameStatsTabs] No ProfileManager in scene — tabs won't bind.");
        }

        private void OnDisable()
        {
            var pm = ProfileManager.Instance;
            if (pm != null) pm.OnProfileChanged -= HandleProfileChanged;
        }

        private async void RequestRefresh()
        {
            try
            {
                var pm = ProfileManager.Instance;
                if (pm != null) await pm.EnsureLoadedAsync();   // OnProfileChanged → Rebuild when it lands
            }
            catch (System.Exception e) { Debug.LogWarning($"[GameStatsTabs] refresh failed: {e.Message}"); }
        }

        private void HandleProfileChanged(UserProfileData _) => Rebuild();

        private void Rebuild()
        {
            var pm = ProfileManager.Instance;
            if (pm == null || tabContainer == null || tabPrefab == null) return;

            // Keep the user on the same tab across a refresh (match by label); default to "All".
            string prevLabel = (_selected >= 0 && _selected < _labels.Count) ? _labels[_selected] : allTabLabel;

            // --- build the ordered source list: All (aggregate) + one per game ---
            _labels.Clear();
            _stats.Clear();

            _labels.Add(allTabLabel);
            _stats.Add(FromAggregate(pm.Stats));

            // Source the game list from the catalog if assigned, else the inline list on this component.
            var defs = (catalog != null && catalog.games != null && catalog.games.Count > 0) ? catalog.games : games;
            if (defs != null && defs.Count > 0)
            {
                // A tab per listed game (played or not). Stats matched by leaderboard id, else zeros.
                foreach (var def in defs)
                {
                    if (def == null) continue;
                    if (!includeComingSoon && !def.available) continue;
                    _labels.Add(string.IsNullOrEmpty(def.displayName) ? def.key : def.displayName);
                    var pg = FindPerGame(pm, def.leaderboardGameId);
                    _stats.Add(pg != null ? FromGame(pg) : default);   // default struct = all zeros
                }
            }
            else
            {
                // Fallback: only the games the player has actually played (server order).
                foreach (var g in pm.PerGame)
                {
                    _labels.Add(string.IsNullOrEmpty(g.DisplayName) ? "Game" : g.DisplayName);
                    _stats.Add(FromGame(g));
                }
            }

            // --- respawn the tab buttons to match (count is small — pooling isn't worth it) ---
            for (int i = 0; i < _tabs.Count; i++)
                if (_tabs[i] != null) Destroy(_tabs[i].gameObject);
            _tabs.Clear();

            for (int i = 0; i < _labels.Count; i++)
            {
                var tab = Instantiate(tabPrefab, tabContainer);
                tab.Setup(i, _labels[i], Select);
                _tabs.Add(tab);
            }

            // --- restore selection (same label) or default to the first tab (All) ---
            int idx = _labels.IndexOf(prevLabel);
            _selected = -1;                          // force a repaint even if the index is unchanged
            SelectInternal(idx >= 0 ? idx : 0, animate: false);   // initial/refresh paint is instant
        }

        /// <summary>Select a tab by index (user action): highlight it + animate its stats into the stat block.</summary>
        public void Select(int index) => SelectInternal(index, animate: true);

        private void SelectInternal(int index, bool animate)
        {
            if (index < 0 || index >= _stats.Count) return;
            if (index == _selected) return;   // re-selecting the active tab is a no-op (Rebuild resets _selected to -1 first)
            _selected = index;

            for (int i = 0; i < _tabs.Count; i++)
                if (_tabs[i] != null) _tabs[i].SetSelected(i == index);

            if (statBlock != null)
            {
                var d = _stats[index];
                statBlock.Bind(in d, animate);
            }
        }

        // ---- mapping helpers ----

        // Find the server per-game stats for a catalog game by its leaderboard id (0 / unmatched → null → zeros).
        private static GameStatsDto FindPerGame(ProfileManager pm, int leaderboardGameId)
        {
            if (leaderboardGameId <= 0) return null;
            foreach (var g in pm.PerGame)
                if (g != null && g.Game == leaderboardGameId) return g;
            return null;
        }

        private static StatBlockView.Stats FromAggregate(ProfileStats s) => new StatBlockView.Stats
        {
            GamesPlayed = s.GamesPlayed,
            GamesWon = s.GamesWon,
            WinRate = s.WinRate,                 // aggregate WinRate is non-null double → fine as double?
            Wagered = s.TotalWagered,
            BiggestWin = s.BiggestWin,
            NetProfit = s.NetProfit,
            CurrentWinStreak = s.CurrentWinStreak,
            LongestWinStreak = s.LongestWinStreak,
            LastPlayed = s.LastPlayedAt,
            StartedPlaying = s.StartedPlayingAt,
        };

        private static StatBlockView.Stats FromGame(GameStatsDto g) => new StatBlockView.Stats
        {
            GamesPlayed = g.GamesPlayed,
            GamesWon = g.GamesWon,
            WinRate = g.WinRate,                 // per-game WinRate is already nullable
            Wagered = g.TotalWagered,
            BiggestWin = g.BiggestWin,
            NetProfit = g.NetProfit,
            CurrentWinStreak = g.CurrentWinStreak,
            LongestWinStreak = g.LongestWinStreak,
            LastPlayed = g.LastPlayedAt,
            StartedPlaying = g.StartedPlayingAt,
        };
    }
}
