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

        /// <summary>
        /// Re-entry guard, held from the deal-tap until the server confirms the round started, so a second tap can't
        /// re-drop a phantom stack and re-bet across the network gap.
        ///
        /// A SELF-EXPIRING DEADLINE, not a bool. The bool version was cleared at the end of a coroutine, which meant
        /// any path that didn't reach that line left Repeat dead for the whole session — and there was one: the wait
        /// re-read the board's betting deadline every frame, and the server re-arms that for the NEXT window after
        /// every round, so a deal that never started a round chased a deadline that kept moving and the loop never
        /// exited. Expressed as a timestamp, the guard cannot outlive itself no matter what happens in between.
        /// </summary>
        private float _dealingUntil;

        private bool Dealing => Time.unscaledTime < _dealingUntil;

        [Tooltip("Absolute cap on the re-entry guard after a deal is fired. It normally clears the moment the round " +
                 "starts; this is the backstop for a deal that never becomes a round (bet refused, request lost). " +
                 "Must comfortably exceed the longest betting window, since a multiplayer deal can wait out the whole " +
                 "window for other players.")]
        [SerializeField] private float dealGuardMaxSeconds = 25f;

        // A disabled object kills its coroutines, so release the guards rather than leaving them latched.
        private void OnDisable()
        {
            _running = null;
            _dealingUntil = 0f;
        }

        /// <summary>True if there's a remembered bet to repeat.</summary>
        public bool CanRepeat => builder != null && builder.LastPlaced.Count > 0;

        /// <summary>Re-drop the last bet's chips onto the local spot, then deal.</summary>
        public void Repeat()
        {
            // Silent bails are undiagnosable from the outside, so each names itself. The deal guard is a self-expiring
            // deadline (see _dealingUntil) precisely because the old latching bool produced the "worked for ten hands
            // then never again" failure; _running is still a handle, so OnDisable releases it.
            if (_running != null) { Debug.LogWarning("[BetRepeater] REPEAT ignored: a previous repeat coroutine is still marked running."); return; }
            if (Dealing) { Debug.LogWarning($"[BetRepeater] REPEAT ignored: a deal fired {dealGuardMaxSeconds - (_dealingUntil - Time.unscaledTime):0.0}s ago has not become a round yet (guard clears in {_dealingUntil - Time.unscaledTime:0.0}s)."); return; }
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
            if (Dealing) { Debug.LogWarning("[BetRepeater] BET-MIN ignored: a deal is still awaiting its round (guard auto-clears)."); return; }
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
            _dealingUntil = 0f;
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
            _dealingUntil = Time.unscaledTime + Mathf.Max(5f, dealGuardMaxSeconds);
            _running = null;
            builder.Deal();   // place the running total + deal

            // Release the guard as soon as the round really starts. The cap above is only the backstop — normally we
            // exit here within a round trip.
            //
            // ⚠️ The window deadline is SNAPSHOT ONCE, deliberately. Re-reading board.BettingExpiresAt each frame was
            // the bug: the server re-arms it for the NEXT window at the end of every round, so a deal that never
            // became a round (bet refused, request lost) chased a deadline that kept moving forward and this loop
            // never ended — leaving the guard latched and Repeat dead for the rest of the session.
            //
            // Still deadline-driven rather than a magic constant, because a multiplayer deal can legitimately sit for
            // the whole betting window waiting on other players; the absolute cap only catches the pathological case.
            var windowEnd = table?.Board?.BettingExpiresAt;
            while (Time.unscaledTime < _dealingUntil)
            {
                if (table?.Board != null && table.Board.RoundInProgress) break;                  // the round started
                if (windowEnd.HasValue && System.DateTimeOffset.UtcNow >= windowEnd.Value.AddSeconds(2f)) break;
                yield return null;
            }
            _dealingUntil = 0f;
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
