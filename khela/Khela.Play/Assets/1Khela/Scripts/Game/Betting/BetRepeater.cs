using System.Collections;
using System.Collections.Generic;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// One-tap re-bet. <b>Repeat</b> re-drops the exact chips from your last dealt bet onto your seat's bet spot —
    /// real physics, staggered so they fall one after another like manual drops — and then deals, so there's no
    /// rebuilding the stack and no second Deal tap. <b>Clear</b> empties the current stack and zeroes the bet. The
    /// last bet is read from <see cref="BetBuilder.LastPlaced"/>; the chip colours come from the <see cref="ChipSet"/>.
    /// Put this next to the <see cref="BetBuilder"/> (e.g. on the "Betting" object).
    /// </summary>
    public sealed class BetRepeater : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [SerializeField] private BetBuilder builder;
        [SerializeField] private ChipSet chipSet;
        [Tooltip("One bet spot per seat — element 0 = seat 1, … Repeat drops onto the LOCAL seat's spot.")]
        [SerializeField] private BetSpot[] spotsBySeat;
        [Tooltip("Delay between each chip drop so they fall one after another.")]
        [SerializeField] private float dropInterval = 0.1f;
        [Tooltip("Pause after the last chip drops, before dealing, so the chips settle on the felt before the gather " +
                 "pulls them into the stack (manual bets already settle while the player taps chips; this covers the " +
                 "instant Repeat / min-bet path).")]
        [SerializeField] private float settleBeforeDeal = 0.25f;

        private Coroutine _running;
        private bool _dealing;   // true from the deal-tap until the server confirms the round started — blocks a double re-bet

        /// <summary>True if there's a remembered bet to repeat.</summary>
        public bool CanRepeat => builder != null && builder.LastPlaced.Count > 0;

        /// <summary>Re-drop the last bet's chips onto the local spot, then deal.</summary>
        public void Repeat()
        {
            if (_running != null || _dealing || !CanRepeat) return;   // already re-betting/dealing → ignore the double-tap
            if (table != null && table.Board != null && table.Board.RoundInProgress) return;   // a round is already live
            var spot = LocalSpot();
            if (spot == null) return;
            _running = StartCoroutine(DropThenDeal(spot, new List<long>(builder.LastPlaced)));   // re-drop the last bet
        }

        /// <summary>Bet the TABLE MINIMUM and deal — the same felt drop + immediate deal as <see cref="Repeat"/>, but
        /// for one minimum-value chip instead of the last bet. Used by the idle-kick warning's BET {min} button so it
        /// behaves exactly like Repeat: chips fall on the felt and the round starts NOW, not when the timer expires.</summary>
        public void BetMinimumAndDeal()
        {
            if (_running != null || _dealing || builder == null) return;
            if (table == null || table.Board == null || table.Board.RoundInProgress) return;
            var spot = LocalSpot();
            if (spot == null) return;
            var values = chipSet != null ? chipSet.Values(table.Board.MinBet, table.Board.MaxBet) : null;
            if (values == null || values.Count == 0) return;
            // values[0] == minBet × 1 == the table minimum, so one lowest-rank chip is exactly the min bet.
            _running = StartCoroutine(DropThenDeal(spot, new List<long> { values[0] }));
        }

        /// <summary>Clear the current stack and zero the bet (cancels an in-progress repeat too).</summary>
        public void Clear()
        {
            if (_running != null) { StopCoroutine(_running); _running = null; }
            _dealing = false;
            if (builder != null) builder.Clear();
        }

        // Shared drop-then-deal used by both Repeat (last bet's chips) and BetMinimumAndDeal (one min chip): drop each
        // chip onto the spot with real physics, staggered, then place the running total and deal immediately.
        private IEnumerator DropThenDeal(BetSpot spot, List<long> chipValues)
        {
            builder.Clear();   // start from an empty stack

            // PrefabsFor, not LevelPrefabs — aligned 1:1 with `values`, since the colour window slides with the table.
            IReadOnlyList<GameObject> prefabs = chipSet != null
                ? chipSet.PrefabsFor(table.Board.MinBet, table.Board.MaxBet) : null;
            var values = (chipSet != null && table != null && table.Board != null)
                ? chipSet.Values(table.Board.MinBet, table.Board.MaxBet) : null;

            foreach (var v in chipValues)
            {
                if (!builder.CanPlace(v)) break;   // can't afford the rest of the bet

                var prefab = PrefabFor(v, values, prefabs);
                if (prefab != null)
                {
                    var chip = Instantiate(prefab);
                    var jitter = Random.insideUnitCircle * 0.03f;       // scatter so they don't stack dead-center
                    chip.transform.position = spot.transform.position + new Vector3(jitter.x, 0f, jitter.y);
                    var view = chip.GetComponentInChildren<ChipView>();
                    if (view != null) view.SetValue(v);
                    spot.Stack(chip);   // physics drop into the tray
                }
                builder.Add(v);
                yield return new WaitForSeconds(dropInterval);
            }

            if (settleBeforeDeal > 0f) yield return new WaitForSeconds(settleBeforeDeal);   // let chips land before the gather pulls them

            // Bridge the re-entry guard across the deal's network gap: after Deal() the round isn't "in progress"
            // until the server replies, and during THAT window a second tap would re-drop a phantom stack + re-bet.
            // Hold _dealing until the board confirms the round started (or a short timeout if the deal never lands).
            _dealing = true;
            _running = null;
            builder.Deal();   // place the running total + deal

            // Hold the re-entry guard until the round actually STARTS — bounded by the board's OWN betting-window
            // deadline, NOT a fixed timeout. With the multiplayer DealAsync HOLD our deal can sit for the whole window
            // waiting on other players, and that window can exceed any magic constant (BettingSeconds is a live admin
            // knob + the presentation ceiling scales with table size), so a fixed cap could clear _dealing mid-hold and
            // let a second Repeat double-drop. Wait while the round hasn't started AND the window deadline (+grace) is
            // still ahead. A null/absent deadline means the round is starting (DealCore nulls it as it sets
            // RoundInProgress), so we release; the deadline is absolute, so this can never wedge.
            while ((table == null || table.Board == null || !table.Board.RoundInProgress)
                   && table?.Board?.BettingExpiresAt is System.DateTimeOffset deadline
                   && System.DateTimeOffset.UtcNow < deadline.AddSeconds(2f))
            {
                yield return null;
            }
            _dealing = false;
        }

        private BetSpot LocalSpot()
        {
            if (spotsBySeat == null || table == null) return null;
            int i = table.MySeat - 1;
            return (i >= 0 && i < spotsBySeat.Length) ? spotsBySeat[i] : null;
        }

        // Match a chip value to its colour prefab via the ChipSet's denomination order; fall back to the lowest
        // rank if the table's denominations changed since the last bet (so a chip still drops).
        private static GameObject PrefabFor(long value, IReadOnlyList<long> values, IReadOnlyList<GameObject> prefabs)
        {
            if (prefabs == null || prefabs.Count == 0) return null;
            if (values != null)
                for (int i = 0; i < values.Count && i < prefabs.Count; i++)
                    if (values[i] == value) return prefabs[i];
            return prefabs[0];
        }
    }
}
