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
            // Silent bails again — and these two guards are STICKY: _running and _dealing are cleared by a later
            // step, so if that step is ever missed they stay set and Repeat is dead for the rest of the session.
            // That is precisely the "worked for ten hands then never again" shape, so name which one is holding.
            if (_running != null) { Debug.LogWarning("[BetRepeater] REPEAT ignored: a previous repeat coroutine is still marked running."); return; }
            if (_dealing) { Debug.LogWarning("[BetRepeater] REPEAT ignored: _dealing is still set from an earlier deal that never confirmed."); return; }
            if (!CanRepeat) { Debug.LogWarning("[BetRepeater] REPEAT ignored: no remembered bet to repeat (LastPlaced empty)."); return; }
            if (table != null && table.Board != null && table.Board.RoundInProgress)
            {
                Debug.LogWarning("[BetRepeater] REPEAT ignored: board says a round is in progress (stale board if the felt looks idle).");
                return;
            }
            var spot = LocalSpot();
            if (spot == null) { Debug.LogWarning("[BetRepeater] REPEAT ignored: no local BetSpot resolved for my seat."); return; }
            _running = StartCoroutine(DropThenDeal(spot, new List<long>(builder.LastPlaced)));   // re-drop the last bet
        }

        /// <summary>Bet the TABLE MINIMUM and deal — the same felt drop + immediate deal as <see cref="Repeat"/>, but
        /// for one minimum-value chip instead of the last bet. Used by the idle-kick warning's BET {min} button so it
        /// behaves exactly like Repeat: chips fall on the felt and the round starts NOW, not when the timer expires.</summary>
        public void BetMinimumAndDeal()
        {
            // Same sticky guards as Repeat — this is the idle-kick popup's BET button, so a silent bail here gets
            // the player evicted from the table while they are pressing the thing meant to save them.
            if (_running != null) { Debug.LogWarning("[BetRepeater] BET-MIN ignored: a previous repeat coroutine is still marked running."); return; }
            if (_dealing) { Debug.LogWarning("[BetRepeater] BET-MIN ignored: _dealing is still set from an earlier deal that never confirmed."); return; }
            if (builder == null) { Debug.LogWarning("[BetRepeater] BET-MIN ignored: no BetBuilder."); return; }
            if (table == null || table.Board == null) { Debug.LogWarning("[BetRepeater] BET-MIN ignored: no board yet."); return; }
            if (table.Board.RoundInProgress) { Debug.LogWarning("[BetRepeater] BET-MIN ignored: board says a round is in progress."); return; }
            var spot = LocalSpot();
            if (spot == null) { Debug.LogWarning("[BetRepeater] BET-MIN ignored: no local BetSpot resolved for my seat."); return; }
            var values = chipSet != null ? chipSet.Values(table.Board.MinBet, table.Board.MaxBet) : null;
            if (values == null || values.Count == 0)
            {
                Debug.LogWarning($"[BetRepeater] BET-MIN ignored: chip ladder empty for MinBet={table.Board.MinBet} MaxBet={table.Board.MaxBet}.");
                return;
            }
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
