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

        private void Build(int slot, int seatIdx, Vector3 baseOffset, long amount, IReadOnlyList<long> values, IReadOnlyList<GameObject> prefabs)
        {
            Clear(slot);
            var anchor = anchorsBySeat[seatIdx];
            if (anchor == null || prefabs == null) return;

            long remaining = amount;
            int placed = 0;
            // Greedy largest-denomination-first, capped at maxChips.
            for (int vi = values.Count - 1; vi >= 0 && placed < maxChips; vi--)
            {
                long v = values[vi];
                var prefab = vi < prefabs.Count ? prefabs[vi] : null;
                if (prefab == null || v <= 0) continue;
                while (remaining >= v && placed < maxChips)
                {
                    var go = Instantiate(prefab, anchor);
                    go.transform.localPosition = baseOffset + new Vector3(0f, stackStep * placed, 0f);
                    go.transform.localRotation = Quaternion.identity;
                    foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false; // visual only
                    var chip = go.GetComponentInChildren<ChipView>();
                    if (chip != null) chip.SetValue(v);
                    _stacks[slot].Add(go);
                    remaining -= v;
                    placed++;
                }
            }

            // The TOP chip (last placed, the visible one) shows the stack's TOTAL value instead of its own denomination.
            if (_stacks[slot].Count > 0)
            {
                var topChip = _stacks[slot][_stacks[slot].Count - 1].GetComponentInChildren<ChipView>();
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
    }
}
