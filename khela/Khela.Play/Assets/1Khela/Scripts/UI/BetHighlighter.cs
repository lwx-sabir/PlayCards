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
        [Tooltip("One highlighter per seat — element 0 = seat 1, element 1 = seat 2, … Shown for the local seat while betting.")]
        [SerializeField] private GameObject[] highlightersBySeat;

        private void OnEnable()
        {
            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);   // board may have arrived before we enabled
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (highlightersBySeat == null) return;
            int mySeat = table != null ? table.MySeat : -1;          // 1-based, -1 if not seated
            bool betting = board != null && !board.RoundInProgress;  // only invite betting between rounds
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
