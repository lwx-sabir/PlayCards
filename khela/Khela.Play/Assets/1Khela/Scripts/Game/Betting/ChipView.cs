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
        [Tooltip("How many digits still fit on ONE line with the K/M suffix beside them. Longer than this and the " +
                 "suffix drops to a second line, so \"90K\" stays inline but \"100K\" becomes 100 over K — a wide " +
                 "label would otherwise overflow the round chip face.")]
        [SerializeField] private int maxInlineDigits = 2;

        public long Value { get; private set; }

        /// <summary>Assign the chip's value at runtime and refresh the center label.</summary>
        public void SetValue(long value)
        {
            Value = value;
            if (label != null) label.text = FormatStacked(value, maxInlineDigits);
        }

        /// <summary>
        /// Chip-face variant of <see cref="Format"/>: identical text, except the K/M suffix moves to a SECOND LINE
        /// once the number part is longer than <paramref name="maxInlineDigits"/> — "90K" stays as-is, "100K" becomes
        /// "100\nK". Only the chip label wants this; <see cref="Format"/> stays single-line because the REPEAT button
        /// and the min-bet labels share it and a newline there would break their layout.
        /// </summary>
        public static string FormatStacked(long value, int maxInlineDigits = 2)
        {
            string text = Format(value);
            if (text.Length == 0) return text;

            char suffix = text[text.Length - 1];
            if (suffix != 'K' && suffix != 'M') return text;   // plain number, nothing to move

            string number = text.Substring(0, text.Length - 1);
            return number.Length > maxInlineDigits ? number + "\n" + suffix : text;
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
