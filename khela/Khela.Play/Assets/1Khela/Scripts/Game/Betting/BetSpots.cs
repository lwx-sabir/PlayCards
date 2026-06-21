using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Drives the per-seat bet spots. There's one <see cref="BetSpot"/> in front of each chair, but only the LOCAL
    /// player's may accept chips. This enables the collider on <c>spotsBySeat[MySeat-1]</c> and disables the rest,
    /// so a dragged chip can only land on the box in front of you (no change needed in the drag controller — it
    /// simply can't raycast a disabled collider). The visible box outlines are untouched (collider-only toggle).
    /// Mirrors <see cref="ChipRailSpawner"/>. Put this on any always-active object (e.g. TableRoot).
    /// </summary>
    public sealed class BetSpots : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("One bet spot per seat — element 0 = seat 1, element 1 = seat 2, … Only the local seat accepts drops.")]
        [SerializeField] private BetSpot[] spotsBySeat;

        private int _activeSeat = -2;   // -2 = never evaluated, so the first board always applies

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
            if (spotsBySeat == null) return;
            int mySeat = table != null ? table.MySeat : -1;   // 1-based, -1 if not seated
            if (mySeat == _activeSeat) return;                // seat unchanged → nothing to toggle
            _activeSeat = mySeat;

            for (int i = 0; i < spotsBySeat.Length; i++)
                if (spotsBySeat[i] != null) spotsBySeat[i].SetAccepting(i == mySeat - 1);
        }
    }
}
