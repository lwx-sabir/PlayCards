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

        private Coroutine _running;

        /// <summary>True if there's a remembered bet to repeat.</summary>
        public bool CanRepeat => builder != null && builder.LastPlaced.Count > 0;

        /// <summary>Re-drop the last bet's chips onto the local spot, then deal.</summary>
        public void Repeat()
        {
            if (_running != null || !CanRepeat) return;
            if (table != null && table.Board != null && table.Board.RoundInProgress) return;   // a round is already live
            var spot = LocalSpot();
            if (spot == null) return;
            _running = StartCoroutine(RepeatRoutine(spot));
        }

        /// <summary>Clear the current stack and zero the bet (cancels an in-progress repeat too).</summary>
        public void Clear()
        {
            if (_running != null) { StopCoroutine(_running); _running = null; }
            if (builder != null) builder.Clear();
        }

        private IEnumerator RepeatRoutine(BetSpot spot)
        {
            builder.Clear();   // start from an empty stack

            var last = new List<long>(builder.LastPlaced);
            var prefabs = chipSet != null ? chipSet.LevelPrefabs : null;
            var values = (chipSet != null && table != null && table.Board != null)
                ? chipSet.Values(table.Board.MinBet, table.Board.MaxBet) : null;

            foreach (var v in last)
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

            _running = null;
            builder.Deal();   // place the running total + deal
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
