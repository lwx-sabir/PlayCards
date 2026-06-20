using TMPro;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// A chip on the table: the 3D chip model plus a center label showing its value. Values are DYNAMIC — the
    /// table decides each chip's value at init (from the table stakes + the player's balance) and calls
    /// <see cref="SetValue"/>, which refreshes the label. This is also the draggable unit the
    /// <see cref="ChipDragController"/> reads (<see cref="Value"/>) when a chip is dropped on the bet spot.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class ChipView : MonoBehaviour
    {
        [Tooltip("World-space text in the center of the chip face — shows the value (TextMeshPro 3D).")]
        [SerializeField] private TMP_Text label;

        public long Value { get; private set; }

        /// <summary>Assign the chip's value at runtime and refresh the center label.</summary>
        public void SetValue(long value)
        {
            Value = value;
            if (label != null) label.text = Format(value);
        }

        /// <summary>Compact money label: 1000→"1K", 1500→"1.5K", 250000→"250K", 1000000→"1M", 2500000→"2.5M".</summary>
        public static string Format(long value)
        {
            if (value >= 1_000_000) return (value / 1_000_000m).ToString("0.##") + "M";
            if (value >= 1_000)     return (value / 1_000m).ToString("0.##") + "K";
            return value.ToString();
        }
    }
}
