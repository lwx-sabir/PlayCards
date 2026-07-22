using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Renders each seat's COMMITTED bet as chip stacks on the felt, driven by the live board — so every player sees
    /// every seat's wager (the board is server-authoritative and pushed to all clients). Split-aware: a seat with two
    /// hands shows TWO stacks, one under each hand, positioned with the SAME centred-split offset the cards use
    /// (<see cref="BlackjackTableView.HandCenterLocal"/>) so each stack sits beneath its hand. A stack appears at the
    /// deal, sits through the round, and clears at settle.
    ///
    /// One WORLD anchor per seat (place on the betting box, flat on the felt — chips stack along its local +Y; orient
    /// it like that seat's CARD anchor so the split offset lines up). The per-hand split is DERIVED, so you don't
    /// author one anchor per hand. Amounts are decomposed into the table's chip denominations and stacked. Put this on
    /// an always-active object. Stacks are visual-only (colliders disabled) so they never block chip drag. The TOP
    /// chip of each stack shows the stack's TOTAL value (the chips below keep their denomination, but they're hidden).
    /// </summary>
    public sealed class BetStacks : MonoBehaviour
    {
        // Per-seat hands we draw stacks for (decision: 2). A rare re-split to 3/4 still lays out cards; the extra
        // hands' bets just don't get a dedicated stack.
        private const int MaxHands = 2;

        [SerializeField] private TableController table;
        [SerializeField] private ChipSet chipSet;
        [Tooltip("The table view — provides the centred split offset so a 2nd hand's stack sits under the 2nd hand.")]
        [SerializeField] private BlackjackTableView tableView;
        [Tooltip("One world anchor per seat — element 0 = seat 1, … Place each on that seat's betting box. Chips " +
                 "stack along the anchor's local +Y, so orient it flat on the felt (matching that seat's CARD anchor facing).")]
        [SerializeField] private Transform[] anchorsBySeat;
        [Tooltip("Optional per-seat amount label (TMP) showing the seat's TOTAL committed bet. Element i = seat i+1.")]
        [SerializeField] private TMP_Text[] labelsBySeat;
        [Tooltip("Vertical gap between stacked chips (local units).")]
        [SerializeField] private float stackStep = 0.02f;
        [Tooltip("Max chips drawn per stack — the amount is decomposed largest-denomination-first, so big bets stay short.")]
        [SerializeField] private int maxChips = 10;

        private List<GameObject>[] _stacks;   // flat: seatIdx * MaxHands + handIdx
        private long[] _lastAmount;            // flat, same indexing
        private int[] _lastHandCount;          // per seat — a change shifts the centred offsets, so force a rebuild

        // Round-end HOLD: when a RoundEndDirector is registered we do NOT clear the committed stacks at settle — the
        // director flies losers to the dealer, pays winners, then calls ReleaseHold. Held as a MonoBehaviour so there's
        // no hard type dependency on the director (mirrors the view's conductor). With NO director this stays inert and
        // the stacks clear at settle exactly as before.
        private MonoBehaviour _settleDirector;
        private bool _held;                       // the director owns our stacks (settle → done); OnBoard ignores pushes while set
        private bool _prevInRound;                // last board's RoundInProgress — to catch the in-round → settle transition
        private decimal _lastMinBet, _lastMaxBet; // stakes cached while live so BuildLooseStack decomposes winnings with the right denominations

        private void Awake()
        {
            int n = anchorsBySeat != null ? anchorsBySeat.Length : 0;
            _stacks = new List<GameObject>[n * MaxHands];
            _lastAmount = new long[n * MaxHands];
            for (int i = 0; i < _stacks.Length; i++) { _stacks[i] = new List<GameObject>(); _lastAmount[i] = -1; }
            _lastHandCount = new int[n];
            for (int i = 0; i < n; i++) _lastHandCount[i] = -1;
        }

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
            if (anchorsBySeat == null || _stacks == null) return;

            bool inRound = board != null && board.RoundInProgress;   // commit shows only while the round runs

            // Round-end HOLD: with a settle director registered, DON'T clear the committed stacks at settle. Latch on
            // the in-round → settle transition and ignore every push until the director calls ReleaseHold — so the
            // stacks survive on the felt for it to fly (losers → dealer, winnings → seats) and then sweep. With NO
            // director this whole block is inert and the stacks clear at settle exactly as before.
            if (_held) return;
            if (_settleDirector != null && _prevInRound && !inRound)
            {
                _held = true;
                _prevInRound = false;
                return;
            }
            _prevInRound = inRound;
            if (inRound && chipSet != null) { _lastMinBet = board.MinBet; _lastMaxBet = board.MaxBet; }   // cache stakes for BuildLooseStack

            IReadOnlyList<long> values = (inRound && chipSet != null) ? chipSet.Values(board.MinBet, board.MaxBet) : null;
            IReadOnlyList<GameObject> prefabs = chipSet != null ? chipSet.LevelPrefabs : null;

            for (int i = 0; i < anchorsBySeat.Length; i++)
            {
                var hands = inRound ? SeatHands(board, i + 1) : null;
                int handCount = (hands != null && hands.Count > 0) ? hands.Count : 1;
                bool handCountChanged = handCount != _lastHandCount[i];   // split happened → offsets shift, rebuild
                _lastHandCount[i] = handCount;

                long seatTotal = 0;
                for (int h = 0; h < MaxHands; h++)
                {
                    int slot = i * MaxHands + h;
                    long amount = (hands != null && h < hands.Count) ? (long)hands[h].Bet : 0;
                    seatTotal += amount;

                    if (!handCountChanged && amount == _lastAmount[slot]) continue;   // unchanged → leave as-is (no flicker)
                    _lastAmount[slot] = amount;

                    if (amount > 0 && values != null && values.Count > 0)
                        Build(slot, i, HandOffset(h, handCount), amount, values, prefabs);
                    else
                        Clear(slot);
                }

                SetLabel(i, seatTotal > 0 ? ChipView.Format(seatTotal) : string.Empty);
            }
        }

        private Vector3 HandOffset(int handIndex, int handCount)
            => tableView != null ? tableView.HandCenterLocal(handIndex, handCount) : Vector3.zero;

        // The seat's hands (split → 2+), or null if the seat is empty.
        private static List<HandView> SeatHands(BoardSnapshot board, int seatNumber)
        {
            if (board.Seats == null) return null;
            foreach (var s in board.Seats)
                if (s.SeatNumber == seatNumber) return s.Player?.Hands;
            return null;
        }

        private void Build(int slot, int seatIdx, Vector3 baseOffset, long amount,
                           IReadOnlyList<long> values, IReadOnlyList<GameObject> prefabs)
        {
            Clear(slot);
            var anchor = anchorsBySeat[seatIdx];
            if (anchor == null) return;
            SpawnStack(anchor, baseOffset, amount, values, prefabs, _stacks[slot]);
        }

        // Shared stack builder: decompose `amount` greedily largest-denomination-first (capped at maxChips), spawn the
        // chips under `parent` climbing +Y by stackStep with colliders OFF (visual only), and make the TOP chip show the
        // stack TOTAL. Used for the committed bet stacks (into _stacks) AND the director's unmanaged winnings stacks.
        private void SpawnStack(Transform parent, Vector3 baseLocalPos, long amount,
                                IReadOnlyList<long> values, IReadOnlyList<GameObject> prefabs, List<GameObject> outList)
        {
            if (parent == null || prefabs == null || values == null || outList == null) return;

            long remaining = amount;
            int placed = 0;
            for (int vi = values.Count - 1; vi >= 0 && placed < maxChips; vi--)
            {
                long v = values[vi];
                var prefab = vi < prefabs.Count ? prefabs[vi] : null;
                if (prefab == null || v <= 0) continue;
                while (remaining >= v && placed < maxChips)
                {
                    var go = Instantiate(prefab, parent);
                    go.transform.localPosition = baseLocalPos + new Vector3(0f, stackStep * placed, 0f);
                    go.transform.localRotation = Quaternion.identity;
                    foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false; // visual only
                    var chip = go.GetComponentInChildren<ChipView>();
                    if (chip != null) chip.SetValue(v);
                    outList.Add(go);
                    remaining -= v;
                    placed++;
                }
            }

            // The TOP chip (last placed, the visible one) shows the stack's TOTAL value instead of its own denomination.
            if (outList.Count > 0)
            {
                var topChip = outList[outList.Count - 1].GetComponentInChildren<ChipView>();
                if (topChip != null) topChip.SetValue(amount);
            }
        }

        private void Clear(int slot)
        {
            if (_stacks == null || slot >= _stacks.Length) return;
            var list = _stacks[slot];
            for (int j = 0; j < list.Count; j++) if (list[j] != null) Destroy(list[j]);
            list.Clear();
        }

        private void SetLabel(int seatIdx, string text)
        {
            if (labelsBySeat != null && seatIdx < labelsBySeat.Length && labelsBySeat[seatIdx] != null)
                labelsBySeat[seatIdx].text = text;
        }

        // ---- Round-end director hooks (ONLY a RoundEndDirector calls these; see that class) ----

        /// <summary>
        /// Register the round-end director. While one is registered, committed stacks are HELD at settle (not cleared)
        /// so the director can fly losers to the dealer and pay winners, then <see cref="ReleaseHold"/>. Idempotent.
        /// </summary>
        public void RegisterSettleDirector(MonoBehaviour director)
        {
            if (director != null) _settleDirector = director;
        }

        /// <summary>Clear the director (disabled/destroyed). If it goes away mid-hold, release so the stacks don't freeze on the felt.</summary>
        public void UnregisterSettleDirector(MonoBehaviour director)
        {
            if (_settleDirector != director) return;
            _settleDirector = null;
            if (_held) ReleaseHold();
        }

        /// <summary>
        /// Hand the director BOTH hand slots' committed chips for a seat (split-aware) WITHOUT destroying them, and
        /// forget them here — reset the sentinels so the same recurring bet rebuilds cleanly next round. The director
        /// flies these to the dealer and destroys them. Returns an empty list for an unknown / unstacked seat.
        /// </summary>
        public IReadOnlyList<GameObject> DetachSeatStacks(int seatNumber)
        {
            var outList = new List<GameObject>();
            int seatIdx = seatNumber - 1;
            if (_stacks == null || anchorsBySeat == null || seatIdx < 0 || seatIdx >= anchorsBySeat.Length) return outList;
            for (int h = 0; h < MaxHands; h++)
            {
                int slot = seatIdx * MaxHands + h;
                if (slot < 0 || slot >= _stacks.Length) continue;
                var list = _stacks[slot];
                for (int j = 0; j < list.Count; j++) if (list[j] != null) outList.Add(list[j]);
                list.Clear();
                _lastAmount[slot] = -1;   // sentinel MUST reset or the same recurring bet won't rebuild next round
            }
            if (seatIdx < _lastHandCount.Length) _lastHandCount[seatIdx] = -1;
            return outList;
        }

        /// <summary>
        /// Build an UNMANAGED winnings stack (never tracked in <c>_stacks</c>, so the board render never touches it)
        /// parented to <paramref name="parent"/> (the dealer's hand). The director flies it to the winner and destroys
        /// it. <paramref name="amount"/> is the seat's net winnings (LastResults.Delta). Empty for a non-positive
        /// amount or a missing chip set. Uses the stakes cached from the last live board so the denominations match.
        /// </summary>
        public IReadOnlyList<GameObject> BuildLooseStack(Transform parent, Vector3 localPos, long amount)
        {
            var outList = new List<GameObject>();
            if (parent == null || amount <= 0 || chipSet == null) return outList;
            SpawnStack(parent, localPos, amount, chipSet.Values(_lastMinBet, _lastMaxBet), chipSet.LevelPrefabs, outList);
            return outList;
        }

        /// <summary>That seat's chip anchor (1-based), bounds-checked; null if unauthored — the director pays winnings here.</summary>
        public Transform ChipAnchor(int seatNumber)
        {
            int idx = seatNumber - 1;
            if (anchorsBySeat == null || idx < 0 || idx >= anchorsBySeat.Length) return null;
            return anchorsBySeat[idx];
        }

        /// <summary>
        /// End the hold: destroy any stacks still on the felt (winners' returned bets, pushes that never moved), reset
        /// every sentinel so the next round rebuilds cleanly, and resume normal board-driven rendering.
        /// </summary>
        public void ReleaseHold()
        {
            _held = false;
            if (_stacks != null)
                for (int slot = 0; slot < _stacks.Length; slot++) { Clear(slot); _lastAmount[slot] = -1; }
            if (_lastHandCount != null)
                for (int i = 0; i < _lastHandCount.Length; i++) _lastHandCount[i] = -1;
        }
    }
}
