using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Drives the per-seat betting chip rails. There is ONE <see cref="ChipRail"/> per seat, each pre-placed with
    /// its own slots for that seat's camera view. This picks the rail for the LOCAL player's seat and fills it
    /// while betting, and clears every rail once the round is dealt — so the chips always sit correctly for
    /// whichever seat you took (a single shared rail only ever lines up from one camera angle).
    ///
    /// Chip values come from the <see cref="ChipSet"/> (minBet × multipliers, dropping any above the table max).
    /// Bet-mode is read straight off the board (<c>!RoundInProgress</c>), so no extra visibility component is
    /// needed — the rail is empty during a round and refilled when the betting window opens.
    /// </summary>
    public sealed class ChipRailSpawner : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [SerializeField] private ChipSet chipSet;
        [Tooltip("One rail per seat — element 0 = seat 1, element 1 = seat 2, … Each ChipRail holds that view's slots.")]
        [SerializeField] private ChipRail[] railsBySeat;

        private decimal _min = -1m, _max = -1m;
        private int _activeSeat = -2;     // -2 = never evaluated (so the first board always refreshes)
        private bool _betting;

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
            if (board == null || chipSet == null || railsBySeat == null) return;

            int mySeat = table != null ? table.MySeat : -1;   // 1-based, -1 if not seated
            bool betting = !board.RoundInProgress;            // chips only show during the betting window
            bool stakesChanged = board.MinBet != _min || board.MaxBet != _max;

            // Nothing that affects the rail changed → leave it as-is (don't rebuild every snapshot).
            if (!stakesChanged && mySeat == _activeSeat && betting == _betting) return;

            _min = board.MinBet;
            _max = board.MaxBet;
            _activeSeat = mySeat;
            _betting = betting;

            Refresh();
        }

        private void Refresh()
        {
            // Clear every rail, then fill only the local seat's rail while betting.
            for (int i = 0; i < railsBySeat.Length; i++)
                if (railsBySeat[i] != null) railsBySeat[i].Clear();

            if (!_betting) return;

            int idx = _activeSeat - 1;
            if (idx < 0 || idx >= railsBySeat.Length) return;   // not seated, or no rail authored for this seat
            var rail = railsBySeat[idx];
            if (rail == null) return;

            var values = chipSet.Values(_min, _max);            // minBet × multipliers, ≤ maxBet
            if (values.Count == 0)
            {
                Debug.LogWarning($"[ChipRailSpawner] no chips for [min={_min}, max={_max}] — min bet is 0 or every " +
                                 "multiplier exceeds the max. Check the ChipSet multipliers.");
                return;
            }

            rail.Spawn(values, chipSet.LevelPrefabs);
            if (values.Count > rail.Capacity)
                Debug.Log($"[ChipRailSpawner] seat {_activeSeat} rail has {rail.Capacity} templates but " +
                          $"{values.Count} chips fit the table — place more templates to show them all.");
        }
    }
}
