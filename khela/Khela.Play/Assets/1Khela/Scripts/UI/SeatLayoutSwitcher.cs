using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Swaps the HUD layout to match the seat the local player is in. You author ONE layout per seat (each a
    /// GameObject holding that view's player cards, positioned by hand, with its own <see cref="SeatPlates"/>
    /// driver). This enables the layout for the local player's current seat and disables the rest — so each
    /// seat-view looks exactly how you placed it, no projection or per-frame math. Put this on a persistent HUD
    /// object (not inside any layout).
    /// </summary>
    public sealed class SeatLayoutSwitcher : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("One layout GameObject per seat — element 0 = local seat 1, element 1 = seat 2, … " +
                 "Each holds that view's hand-placed cards + a SeatPlates driver.")]
        [SerializeField] private GameObject[] layoutsByLocalSeat;

        private void OnEnable()
        {
            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (layoutsByLocalSeat == null) return;
            int mySeat = table != null ? table.MySeat : -1;   // 1-based, or -1 if not seated

            for (int i = 0; i < layoutsByLocalSeat.Length; i++)
            {
                if (layoutsByLocalSeat[i] == null) continue;
                bool active = (i == mySeat - 1);               // layout index 0 → seat 1, etc.
                if (layoutsByLocalSeat[i].activeSelf != active)
                    layoutsByLocalSeat[i].SetActive(active);
            }
        }
    }
}
