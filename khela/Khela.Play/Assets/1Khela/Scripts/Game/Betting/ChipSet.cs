using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Chip config for the betting rail. Two parts:
    ///  • <see cref="LevelPrefabs"/> — the colour-rank chip prefabs, low → high. The i-th shown chip uses
    ///    LevelPrefabs[i] (lowest = LevelPrefabs[0]).
    ///  • <see cref="MinMultipliers"/> — chip values as multiples of the table's MIN bet, low → high.
    ///
    /// At table init each chip's value = <c>minBet × multiplier</c> (<see cref="Values"/>), so the chips scale
    /// with the table yet stay fine-grained. e.g. {1, 1.5, 2, 2.5, 3} → a 5k-min table shows 5k, 7.5k, 10k,
    /// 12.5k, 15k; a 1k-min table shows 1k, 1.5k, 2k, 2.5k, 3k. Anything above the table max is dropped (you
    /// can't place a chip bigger than the whole max bet). The server stays denomination-agnostic — the UI just
    /// sums placed chips into one bet <c>amount</c> (validated against the board's MinBet/MaxBet).
    ///
    /// Create via <b>Khela ▸ Chip Set</b>; drop your 6 colour chip prefabs into Level Prefabs (low → high).
    /// Multipliers default to {1, 1.5, 2, 2.5, 3} even if the list is left empty — fill it only to customise.
    /// </summary>
    [CreateAssetMenu(menuName = "Khela/Chip Set", fileName = "ChipSet")]
    public sealed class ChipSet : ScriptableObject
    {
        [Tooltip("Colour-rank chip prefabs, low → high (Chip_L1..L6). The i-th shown chip uses element i. " +
                 "Each prefab needs a ChipView + a Collider on the Chip layer.")]
        [SerializeField] private List<GameObject> levelPrefabs = new List<GameObject>();

        [Tooltip("Chip values as multiples of the table's MIN bet, low → high. {1, 1.5, 2, 2.5, 3} on a 5k-min " +
                 "table → 5k, 7.5k, 10k, 12.5k, 15k. One entry per chip. Leave empty to use the default ladder.")]
        [SerializeField] private List<float> minMultipliers = new List<float> { 1f, 1.5f, 2f, 2.5f, 3f };

        private static readonly float[] DefaultMultipliers = { 1f, 1.5f, 2f, 2.5f, 3f };

        /// <summary>Colour-rank prefabs, low → high. The i-th shown chip uses <c>LevelPrefabs[i]</c>.</summary>
        public IReadOnlyList<GameObject> LevelPrefabs => levelPrefabs;

        /// <summary>Chip values as multiples of the min bet, low → high.</summary>
        public IReadOnlyList<float> MinMultipliers => minMultipliers;

        /// <summary>
        /// The chip values for this table: <c>minBet × each multiplier</c>, low → high, dropping any that exceed
        /// the table max (can't be placed). Falls back to {1, 1.5, 2, 2.5, 3} if no multipliers are authored.
        /// </summary>
        public List<long> Values(decimal minBet, decimal maxBet)
        {
            var result = new List<long>();
            if (minBet <= 0m) return result;

            IReadOnlyList<float> mults =
                (minMultipliers != null && minMultipliers.Count > 0) ? minMultipliers : DefaultMultipliers;

            foreach (var m in mults)
            {
                if (m <= 0f) continue;
                long v = (long)(minBet * (decimal)m);
                if (v <= 0) continue;
                if (maxBet > 0m && v > maxBet) continue;   // a chip bigger than the whole max bet is unplaceable
                result.Add(v);
            }
            return result;
        }

#if UNITY_EDITOR
        [ContextMenu("Fill Default Multipliers (1, 1.5, 2, 2.5, 3)")]
        private void FillDefaultMultipliers()
        {
            minMultipliers = new List<float> { 1f, 1.5f, 2f, 2.5f, 3f };
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
