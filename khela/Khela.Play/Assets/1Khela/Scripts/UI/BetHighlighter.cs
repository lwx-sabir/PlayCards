using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Shows the bet-spot highlighter for the LOCAL player's seat during the betting window, and hides it once the
    /// round deals (or when not seated / spectating). There's one highlighter per seat (pre-placed for each camera
    /// view); this just toggles the right one on. Each highlighter's own <see cref="UiPulse"/> makes it breathe
    /// while shown. Mirrors SeatLayoutSwitcher / BetSpots — put this on an always-active object (e.g. the InfoCanvas).
    /// </summary>
    public sealed class BetHighlighter : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("The table view — used to hold the bet glow through the round-end ceremony. Optional (auto-found).")]
        [SerializeField] private BlackjackTableView view;
        [Tooltip("One highlighter per seat — element 0 = seat 1, element 1 = seat 2, … Shown for the local seat while betting.")]
        [SerializeField] private GameObject[] highlightersBySeat;

        private bool _lastSettling;
        private bool _lastCommitted;   // betting commit flips on the button press, between board pushes

        private void OnEnable()
        {
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();
            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);   // board may have arrived before we enabled
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        // The round-end ceremony ends BETWEEN pushes (the director drops its hold, no board fires), so watch
        // RoundEndSettling too — else the bet glow would stay dark until the next server push after the ceremony.
        private void Update()
        {
            // Both of these flip BETWEEN board pushes — the ceremony ends on the director's own clock, and the betting
            // commit happens on the local button press — so neither can wait for a server push to be noticed.
            bool settling = view != null && view.RoundEndSettling;
            bool committed = table != null && table.BettingCommitted;
            if (settling != _lastSettling || committed != _lastCommitted)
            {
                _lastSettling = settling;
                _lastCommitted = committed;
                OnBoard(table != null ? table.Board : null);
            }
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (highlightersBySeat == null) return;
            int mySeat = table != null ? table.MySeat : -1;          // 1-based, -1 if not seated
            // Only between rounds AND once the previous round-end has fully finished. A blackjack settles the round
            // INSTANTLY (RoundInProgress flips false while the payout ceremony is still playing), so without the
            // settling guard the bet glow would pop back on over the payout — same bug the bet bar had.
            // BettingOpenForMe, not !RoundInProgress — the shared window keeps the round "not started" until the last
            // seat bets, which kept the bet spot glowing at a player who had already committed.
            bool betting = table != null && table.BettingOpenForMe && !(view != null && view.RoundEndSettling);
            int active = betting ? mySeat - 1 : -1;                  // index to show, or -1 for none

            for (int i = 0; i < highlightersBySeat.Length; i++)
            {
                if (highlightersBySeat[i] == null) continue;
                bool show = i == active;
                if (highlightersBySeat[i].activeSelf != show) highlightersBySeat[i].SetActive(show);
            }
        }
    }
}
