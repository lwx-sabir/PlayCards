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

        /// <summary>Fired the instant a DEAL is committed, carrying the bet amount, BEFORE the bet is cleared — so the
        /// chip visuals can hand the dropped pile off to a gather animation before <see cref="OnBetChanged"/>(0)
        /// would wipe it. Distinct from OnBetChanged(0), which also fires on a plain CLEAR/UNDO (no gather wanted).</summary>
        public event Action<long> OnDealCommitted;

        /// <summary>
        /// Fired when a committed bet is REFUSED by the server, carrying the amount that was announced by
        /// <see cref="OnDealCommitted"/>. Everything that ran ahead of the server on that event — the chips gathered
        /// onto the felt, the amount badge, the balance receipt — undoes itself here.
        ///
        /// It exists because the commit is optimistic by design: the chips are pulled into the stack the instant the
        /// player taps, one or two round trips before the server has agreed. The common refusal is losing the race to
        /// the end of the betting window ("cannot change bets during an active round"), which left a stake sitting on
        /// the felt for a round the player was not in.
        /// </summary>
        public event Action<long> OnBetRejected;

        /// <summary>
        /// Raise a commit/reject event without letting a listener break the deal.
        ///
        /// These are consumed by PRESENTATION — the chip gather, the balance receipt, sound. The deal is the money
        /// path. A cosmetic listener throwing (a coroutine started on a disabled object is the classic one) must never
        /// abort the bet or leave the controls locked, so each is isolated and the failure is logged rather than
        /// propagated. Listeners are invoked individually so one bad one doesn't rob the rest.
        /// </summary>
        private static void Announce(Action<long> evt, long amount)
        {
            if (evt == null) return;
            foreach (var d in evt.GetInvocationList())
            {
                try { ((Action<long>)d)(amount); }
                catch (Exception ex) { Debug.LogError($"[BetBuilder] listener {d.Method.DeclaringType?.Name}.{d.Method.Name} threw: {ex}"); }
            }
        }

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
            if (!CanPlace(chipValue))
            {
                // The chip still animates onto the felt from the drag/repeat path, so a refusal here is invisible:
                // the player sees their bet, the total stays 0, and DEAL does nothing. Never fail this quietly.
                Debug.LogWarning($"[BetBuilder] chip {chipValue} REFUSED: Total={Total} + {chipValue} > Cap={Cap} " +
                                 $"(MaxBet={MaxBet}, Balance={Balance}). The chip will still appear on the felt.");
                return;
            }
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
            // Every one of these bails SILENTLY, which is why "deal does nothing" is impossible to diagnose from the
            // outside: the chips still visibly drop (that animation is a separate path from the bet total), so the
            // table looks ready while the button is inert. Say WHY — the device log is the only view we get.
            if (_dealing) { Debug.LogWarning("[BetBuilder] DEAL ignored: a deal is already in flight (_dealing)."); return; }
            if (table == null) { Debug.LogWarning("[BetBuilder] DEAL ignored: no TableController."); return; }
            if (!MeetsMinimum)
            {
                Debug.LogWarning($"[BetBuilder] DEAL ignored: bet below minimum. Total={Total} MinBet={MinBet} " +
                                 $"MaxBet={MaxBet} Balance={Balance} Cap={Cap} placedChips={_placed.Count}. " +
                                 "Total 0 with chips on the felt means Add() was refused by CanPlace — check Cap/Balance.");
                return;
            }
            if (table.Board != null && table.Board.RoundInProgress)
            {
                Debug.LogWarning("[BetBuilder] DEAL ignored: the board says a round is already in progress. " +
                                 "If the felt looks idle, this board is stale — the hub push or resync has stopped.");
                return;
            }
            _ = DealRoutine();
        }

        private async Task DealRoutine()
        {
            _dealing = true;

            try
            {
                // INSIDE the try. This sits between `_dealing = true` and the finally that clears it, so if it ever
                // throws the flag latches and DEAL is dead for the rest of the session — the same wedge BetRepeater
                // had. Anything after the flag is set belongs under the finally, without exception.
                //
                // Locking the bet controls on the PRESS is the point: it used to happen inside table.Deal(), a round
                // trip later, so DEAL and REPEAT stayed lit while `_dealing` silently swallowed every further tap.
                if (table != null) table.CommitBetting();

                var amount = Total;
                _lastPlaced.Clear();
                _lastPlaced.AddRange(_placed);   // remember the chips so Repeat can re-drop the same bet
                Announce(OnDealCommitted, (long)amount);   // let the felt gather the dropped chips BEFORE Clear() wipes them
                Clear();
                // A bet that never landed must NOT fall through to a deal: the server would hold it (this seat hasn't
                // actively bet), so the chips sit on the felt, no round starts, and the controls stay greyed — the
                // "it bet but the deal is pending" state. Hand the controls straight back instead.
                if (!await table.PlaceBet(amount))   // records the bet (debited at deal)
                {
                    Debug.LogWarning("[BetBuilder] DEAL aborted: the bet was rejected, so no deal was requested.");
                    // Roll back everything the optimistic commit above already showed — the gathered chips, the amount
                    // badge and the balance receipt. Waiting for the next board push is not enough: the seat is skipped
                    // while its gather animation is still running, so the phantom stake can outlive several snapshots.
                    Announce(OnBetRejected, (long)amount);
                    table.ReleaseBetting();
                    return;
                }

                // The BET can start the round on its own. Once every seated player has actively bet, the server's
                // round-driver deals on its next tick without anyone pressing Deal (the all-bet backstop) — so by the
                // time the stake lands the round may already be live. Asking to deal then is rejected with "A round is
                // already in progress", which is harmless but surfaces to the player as a failed action. The board we
                // just got back from PlaceBet is authoritative, so if it says we're live there is nothing left to ask
                // for. Repeat hits this far more than a manual deal: its chip-drop animation delays the stake, which
                // lands the bet much closer to a driver tick.
                if (table.Board == null || !table.Board.RoundInProgress)
                    await table.Deal();
            }
            finally { _dealing = false; }
        }
    }
}
