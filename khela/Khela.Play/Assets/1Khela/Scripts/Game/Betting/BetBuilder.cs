using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlayCard.Game.Table;
using PlayCard.Game.Wallet;
using TMPro;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Chip-bet accumulation logic (no UI). Put it on the betting panel and assign the TableController.
    /// <see cref="ChipDragController"/> calls <see cref="Add"/> when a chip is dropped on the bet spot; the
    /// bet-spot / total text bind <see cref="OnBetChanged"/>; the DEAL button calls <see cref="Deal"/>. The rail
    /// itself is built by <see cref="ChipRailSpawner"/> (which owns the ChipSet) — not here.
    ///
    /// Enforces: the running bet never exceeds the table max or the player's balance (<see cref="Cap"/>); DEAL
    /// is allowed only at/above the table min. The server re-validates every bet — this is UX gating only.
    /// </summary>
    public sealed class BetBuilder : MonoBehaviour
    {
        [SerializeField] private TableController table;

        [Header("Display (optional)")]
        [Tooltip("Optional: a TMP label updated with the running bet total.")]
        [SerializeField] private TMP_Text totalLabel;
        [SerializeField] private string totalFormat = "#,0";

        /// <summary>The running bet (sum of placed chips).</summary>
        public decimal Total { get; private set; }

        /// <summary>Fired whenever the bet changes (place / undo / clear). Bind the bet-spot/extra UI to it.</summary>
        public event Action<decimal> OnBetChanged;

        private void Notify()
        {
            if (totalLabel != null) totalLabel.text = Total.ToString(totalFormat);
            OnBetChanged?.Invoke(Total);
        }

        private readonly List<long> _placed = new List<long>();     // placed chip values, for Undo
        private readonly List<long> _lastPlaced = new List<long>(); // chips of the last dealt bet, for Repeat
        private bool _dealing;                                        // a deal is in flight — drop re-entrant DEAL taps

        /// <summary>The exact chip values of the last dealt bet, for one-tap Repeat. Empty until the first deal.</summary>
        public IReadOnlyList<long> LastPlaced => _lastPlaced;

        /// <summary>Total amount of the last dealt bet (sum of <see cref="LastPlaced"/>); 0 until the first deal.</summary>
        public long LastBet { get { long s = 0; for (int i = 0; i < _lastPlaced.Count; i++) s += _lastPlaced[i]; return s; } }

        public decimal MinBet => table != null && table.Board != null ? table.Board.MinBet : 0m;
        public decimal MaxBet => table != null && table.Board != null ? table.Board.MaxBet : 0m;
        public decimal Balance => WalletManager.Instance != null ? WalletManager.Instance.Chips : 0m;

        /// <summary>The most a bet may reach: the table max, capped by what the player can afford.</summary>
        public decimal Cap => MaxBet > 0m ? Math.Min(MaxBet, Balance) : Balance;

        /// <summary>True once the bet meets the table minimum, so DEAL is allowed.</summary>
        public bool MeetsMinimum => Total > 0m && (MinBet <= 0m || Total >= MinBet);

        /// <summary>True if a chip of this value can still be placed without exceeding the cap.</summary>
        public bool CanPlace(long chipValue) => chipValue > 0 && Total + chipValue <= Cap;

        // ---- bet mutations (Add is called by ChipDragController on a valid drop; the rest are UnityEvent-friendly) ----

        /// <summary>Place a chip of this (runtime) value. No-op if it would exceed the table max / balance.</summary>
        public void Add(long chipValue)
        {
            if (!CanPlace(chipValue)) return;
            _placed.Add(chipValue);
            Total += chipValue;
            Notify();
        }

        /// <summary>Remove the last placed chip (hook an UNDO button).</summary>
        public void Undo()
        {
            if (_placed.Count == 0) return;
            Total -= _placed[_placed.Count - 1];
            _placed.RemoveAt(_placed.Count - 1);
            Notify();
        }

        /// <summary>Clear the whole bet (hook a CLEAR button).</summary>
        public void Clear()
        {
            if (_placed.Count == 0 && Total == 0m) return;
            _placed.Clear();
            Total = 0m;
            Notify();
        }

        /// <summary>Place the accumulated bet and deal, then clear (hook the DEAL button). No-op below the min,
        /// while a deal is already in flight, or while a round is already running — so rapid/queued DEAL taps
        /// (e.g. after a lag spike) can't fire several rounds back-to-back.</summary>
        public void Deal()
        {
            if (_dealing || !MeetsMinimum || table == null) return;
            if (table.Board != null && table.Board.RoundInProgress) return;   // a round is already live
            _ = DealRoutine();
        }

        private async Task DealRoutine()
        {
            _dealing = true;
            try
            {
                var amount = Total;
                _lastPlaced.Clear();
                _lastPlaced.AddRange(_placed);   // remember the chips so Repeat can re-drop the same bet
                Clear();
                await table.PlaceBet(amount);    // records the bet (debited at deal)
                await table.Deal();
            }
            finally { _dealing = false; }
        }
    }
}
