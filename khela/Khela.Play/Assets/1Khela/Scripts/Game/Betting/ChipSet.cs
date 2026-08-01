using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Chip config for the betting rail. Two parts:
    ///  • <see cref="LevelPrefabs"/> — the colour-rank chip prefabs, low → high (L1..L6).
    ///  • <see cref="DenominationLadder"/> — a FIXED ladder of real chip values, ascending.
    ///
    /// A table shows <see cref="chipsPerTable"/> denominations: the LOWEST ladder entries inside [minBet, maxBet].
    /// There is one MORE colour rank than a table shows, reserved for high tables: normally the rail renders L1-L5,
    /// and only once the HIGHEST CHIP on the table reaches <see cref="topRankThreshold"/> does it shift to L2-L6.
    /// Always pair values with <see cref="PrefabsFor"/>, never by indexing LevelPrefabs.
    ///
    /// WHY A LADDER, NOT MULTIPLES OF THE MIN: multiplying the min bet by {1, 1.5, 2, 2.5, …} produces values no
    /// casino would ever mint — a 25K table gave 62,500, which the chip face renders as "62.5K". Real chips are
    /// round numbers, so the values are authored directly here and simply filtered by the table's limits. The ladder
    /// is the ONLY source of denominations: whatever is in it is legal, and nothing is computed or rejected.
    ///
    /// The server stays denomination-agnostic — the UI just sums placed chips into one bet <c>amount</c>
    /// (validated against the board's MinBet/MaxBet), so changing this ladder needs no server change.
    ///
    /// Create via <b>Khela ▸ Chip Set</b>. Right-click the asset ▸ <b>Fill Default Ladder</b> to (re)generate.
    /// </summary>
    [CreateAssetMenu(menuName = "Khela/Chip Set", fileName = "ChipSet")]
    public sealed class ChipSet : ScriptableObject
    {
        [Tooltip("Colour-rank chip prefabs, low → high (Chip_L1..L6). The i-th shown chip uses element i. " +
                 "Each prefab needs a ChipView + a Collider on the Chip layer. This count also caps how many " +
                 "denominations a table can show.")]
        [SerializeField] private List<GameObject> levelPrefabs = new List<GameObject>();

        [Tooltip("Real chip values, ascending. A table shows the lowest of these that fit inside its min/max. " +
                 "Every value you put here is used as-is — nothing is rounded, scaled or rejected.")]
        [SerializeField] private List<long> denominationLadder = new List<long>();

        [Tooltip("Uniform scale for every chip sitting ON THE FELT — dropped chips, committed stacks, repeat bets and " +
                 "paid winnings alike. One value so a chip never changes size as it moves between them (the rail is " +
                 "separate: those are sized by their slot templates for that seat's camera view).")]
        [SerializeField] private float feltChipScale = 1.2f;

        [Tooltip("The top colour rank (L6) is reserved for high tables: it is only used when the HIGHEST CHIP this " +
                 "table shows is at least this much — not when the table's max BET is. Below it the rail renders " +
                 "L1-L5; at or above it the window slides one rank to L2-L6 and L1 is skipped.")]
        [SerializeField] private long topRankThreshold = 500_000;

        [Tooltip("How many denominations ONE table shows. Fewer than Level Prefabs on purpose: the extra rank(s) are " +
                 "headroom for the window to SLIDE. With 6 prefabs and 5 per table, a low table shows L1-L5, and a " +
                 "table whose minimum has cut the smallest chips off shows L2-L6.")]
        [SerializeField] private int chipsPerTable = 5;

        // {1, 2, 3, 5, 7} x 10^n from 500 up. Round, evenly spaced in feel, every value a multiple of 500.
        private static readonly long[] DefaultLadder =
        {
            500,
            1_000, 2_000, 3_000, 5_000, 7_000,
            10_000, 20_000, 30_000, 50_000, 70_000,
            100_000, 200_000, 300_000, 500_000, 700_000,
            1_000_000, 2_000_000, 3_000_000, 5_000_000, 7_000_000,
            10_000_000, 20_000_000, 30_000_000, 50_000_000, 70_000_000,
            100_000_000, 200_000_000, 300_000_000, 500_000_000, 700_000_000,
            1_000_000_000,
        };

        /// <summary>Colour-rank prefabs, low → high. The i-th shown chip uses <c>LevelPrefabs[i]</c>.</summary>
        public IReadOnlyList<GameObject> LevelPrefabs => levelPrefabs;

        /// <summary>Uniform scale for chips on the felt — the single value every felt spawner applies, so a chip is
        /// the same size whether it was dropped by hand, built into a committed stack, or paid out as winnings.</summary>
        public float FeltChipScale => feltChipScale > 0f ? feltChipScale : 1f;

        /// <summary>The authored chip values, ascending (falls back to the default ladder when empty).</summary>
        public IReadOnlyList<long> DenominationLadder =>
            (denominationLadder != null && denominationLadder.Count > 0) ? denominationLadder : DefaultLadder;

        /// <summary>The chip values for this table, ascending. See the overload for the colour-rank offset.</summary>
        public List<long> Values(decimal minBet, decimal maxBet) => Values(minBet, maxBet, out _);

        /// <summary>
        /// The colour prefabs for this table, ALIGNED 1:1 with <see cref="Values(decimal, decimal)"/> — element i is
        /// the prefab for value i. Use this instead of <see cref="LevelPrefabs"/> anywhere you pair a value with a
        /// chip: the denomination window slides up the ladder on richer tables, so the i-th value is NOT generally
        /// the i-th colour rank. Indexing LevelPrefabs directly is what pinned every table to L1 and made the top
        /// rank unreachable.
        /// </summary>
        public List<GameObject> PrefabsFor(decimal minBet, decimal maxBet)
        {
            var values = Values(minBet, maxBet, out int offset);
            var list = new List<GameObject>(values.Count);
            int count = levelPrefabs != null ? levelPrefabs.Count : 0;
            for (int i = 0; i < values.Count; i++)
            {
                int rank = i + offset;
                list.Add(rank >= 0 && rank < count ? levelPrefabs[rank] : null);
            }
            return list;
        }

        /// <summary>
        /// The chip values for this table: the LOWEST ladder entries within [minBet, maxBet], ascending, at most
        /// <see cref="chipsPerTable"/> of them.
        /// </summary>
        /// <param name="prefabOffset">
        /// Where this table's window sits in the COLOUR ranks: the i-th shown chip uses
        /// <c>LevelPrefabs[i + prefabOffset]</c>. 0 for a normal table (L1-L5); 1 once the HIGHEST chip on the table
        /// reaches <see cref="topRankThreshold"/>, which brings L6 in and drops L1 (L2-L6).
        /// </param>
        public List<long> Values(decimal minBet, decimal maxBet, out int prefabOffset)
        {
            prefabOffset = 0;
            var result = new List<long>();
            if (minBet <= 0m) return result;

            var ladder = DenominationLadder;
            int prefabCount = levelPrefabs != null ? levelPrefabs.Count : 0;
            int shown = chipsPerTable > 0 ? chipsPerTable : (prefabCount > 0 ? prefabCount : ladder.Count);
            if (prefabCount > 0) shown = Mathf.Min(shown, prefabCount);

            for (int i = 0; i < ladder.Count && result.Count < shown; i++)
            {
                long v = ladder[i];
                if (v <= 0) continue;
                if ((decimal)v < minBet) continue;
                if (maxBet > 0m && (decimal)v > maxBet) continue;  // a chip bigger than the whole max bet is unplaceable
                result.Add(v);
            }

            // The top rank is decided by the HIGHEST CHIP on this table, not by how much of the ladder sat below the
            // minimum (that slid on nearly every table, because the ladder starts far below any real min) and not by
            // the table's max BET. Only once the biggest chip shown reaches the threshold does L6 come into play, and
            // the window moves up one rank so L1 drops off.
            int headroom = Mathf.Max(0, prefabCount - shown);
            bool useTopRank = result.Count > 0 && topRankThreshold > 0 && result[result.Count - 1] >= topRankThreshold;
            prefabOffset = useTopRank ? Mathf.Min(1, headroom) : 0;
            return result;
        }

#if UNITY_EDITOR
        [ContextMenu("Fill Default Ladder (1,2,3,5,7 x 10^n from 500)")]
        private void FillDefaultLadder()
        {
            denominationLadder = new List<long>(DefaultLadder);
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
