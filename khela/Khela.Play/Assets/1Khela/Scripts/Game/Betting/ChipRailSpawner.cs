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
    /// Bet-mode is <c>!RoundInProgress</c> AND not <see cref="BlackjackTableView.RoundEndSettling"/> — so the rail is
    /// empty during a round AND through the whole round-end ceremony, and refills only once betting truly reopens.
    /// </summary>
    public sealed class ChipRailSpawner : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [SerializeField] private ChipSet chipSet;
        [Tooltip("The table view — keeps the rail EMPTY through the round-end ceremony (RoundEndSettling), not just while " +
                 "RoundInProgress. Auto-found if empty.")]
        [SerializeField] private BlackjackTableView view;
        [Tooltip("One rail per seat — element 0 = seat 1, element 1 = seat 2, … Each ChipRail holds that view's slots.")]
        [SerializeField] private ChipRail[] railsBySeat;

        private decimal _min = -1m, _max = -1m;
        private int _activeSeat = -2;     // -2 = never evaluated (so the first board always refreshes)
        private bool _betting;

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

        // RoundEndSettling ends BETWEEN board pushes (the director drops its hold, no board fires), so re-evaluate each
        // frame — else the rail would pop early or lag a push behind the ceremony. Cheap: OnBoard early-outs when
        // nothing that affects the rail changed.
        private void Update()
        {
            if (table != null && table.Board != null) OnBoard(table.Board);
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (board == null || chipSet == null || railsBySeat == null) return;

            int mySeat = table != null ? table.MySeat : -1;   // 1-based, -1 if not seated
            // Betting window = between rounds AND the previous round-end has fully finished. A blackjack settles the
            // round INSTANTLY (RoundInProgress flips false while the payout ceremony is still playing), so gating on
            // RoundInProgress alone popped the chip rail up over the payout.
            bool betting = !board.RoundInProgress && !(view != null && view.RoundEndSettling);
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
            if (idx < 0 || idx >= railsBySeat.Length)
            {
                Debug.LogWarning($"[ChipRailSpawner] no rail filled — MySeat={_activeSeat} (idx {idx}) is outside " +
                                 $"railsBySeat[0..{railsBySeat.Length}). Either you're not seated (seat unresolved) or no " +
                                 "rail element was authored for this seat. railsBySeat[0]=seat 1, [1]=seat 2, …");
                return;
            }
            var rail = railsBySeat[idx];
            if (rail == null)
            {
                Debug.LogWarning($"[ChipRailSpawner] railsBySeat[{idx}] (seat {_activeSeat}) is NOT assigned — drop the " +
                                 "ChipRail for this seat into that slot.");
                return;
            }
            if (chipSet.LevelPrefabs == null || chipSet.LevelPrefabs.Count == 0)
            {
                Debug.LogWarning("[ChipRailSpawner] the ChipSet has no Level Prefabs — assign the chip prefabs " +
                                 "(low→high) on the ChipSet asset, or no chips can spawn.");
                return;
            }

            var values = chipSet.Values(_min, _max);            // minBet × multipliers, ≤ maxBet
            if (values.Count == 0)
            {
                Debug.LogWarning($"[ChipRailSpawner] no chips for [min={_min}, max={_max}] — min bet is 0 or every " +
                                 "multiplier exceeds the max. If min/max are 0 the table data is stale (reseed the lobby); " +
                                 "otherwise check the ChipSet multipliers.");
                return;
            }

            rail.Spawn(values, chipSet.LevelPrefabs);
            if (values.Count > rail.Capacity)
                Debug.Log($"[ChipRailSpawner] seat {_activeSeat} rail has {rail.Capacity} templates but " +
                          $"{values.Count} chips fit the table — place more templates to show them all.");
        }
    }
}
