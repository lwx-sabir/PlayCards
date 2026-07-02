using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One tab in the profile game-stats tab bar (All / Blackjack / Slot / Three Card Poker / …). Spawned from a
    /// prefab by <see cref="GameStatsTabs"/>. Follows the project's selection idiom (<c>SetSelected(bool)</c>, like
    /// <c>ICarouselItem</c>) — here the active/inactive look is a COLOUR swap on a graphic (no extra GameObjects,
    /// no Unity ToggleGroup).
    ///
    /// WIRING (on the tab prefab): assign the <see cref="label"/> (TMP_Text). The tab tints
    /// <see cref="colorTarget"/> between <see cref="selectedColor"/> and <see cref="unselectedColor"/> on selection;
    /// leave <see cref="colorTarget"/> empty to tint the label itself, or point it at a background Image instead.
    /// The <see cref="button"/> auto-resolves from this GameObject if left empty.
    /// </summary>
    public sealed class GameStatsTabButton : MonoBehaviour
    {
        [SerializeField] private TMP_Text label;
        [Tooltip("The graphic tinted by selection. Leave empty to tint the label; or assign a background Image to tint that.")]
        [SerializeField] private Graphic colorTarget;
        [Tooltip("Colour when this tab is the active one.")]
        [SerializeField] private Color selectedColor = Color.white;
        [Tooltip("Colour when this tab is NOT active.")]
        [SerializeField] private Color unselectedColor = new Color(0.55f, 0.66f, 0.85f, 1f);
        [Tooltip("The clickable Button. Auto-resolved from this object if left empty.")]
        [SerializeField] private Button button;

        private int _index;
        private Action<int> _onClick;

        /// <summary>Configure the tab: its label text, its index, and the click callback. Starts deselected.</summary>
        public void Setup(int index, string text, Action<int> onClick)
        {
            _index = index;
            _onClick = onClick;
            if (label != null) label.text = text;
            if (button == null) button = GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => _onClick?.Invoke(_index));
            }
            SetSelected(false);
        }

        /// <summary>Tint the target graphic for the active / inactive state.</summary>
        public void SetSelected(bool on)
        {
            Graphic g = colorTarget != null ? colorTarget : label;   // TMP_Text is a Graphic
            if (g != null) g.color = on ? selectedColor : unselectedColor;
        }
    }
}
