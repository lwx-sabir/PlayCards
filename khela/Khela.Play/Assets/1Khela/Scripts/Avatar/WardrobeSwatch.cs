using System;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    /// <summary>
    /// One colour swatch in a palette row — a coloured button with an optional "selected" highlight.
    /// <see cref="WardrobePaletteRow"/> spawns one per swatch and binds it. Pure view: the prefab IS the state.
    /// Build it as a Button with an Image (tinted to the colour) and a child highlight GameObject (hidden by default).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeSwatch : MonoBehaviour
    {
        [Tooltip("The Image tinted to the swatch colour.")]
        [SerializeField] private Image swatch;
        [Tooltip("Shown only while this swatch is the selected one (outline / checkmark).")]
        [SerializeField] private GameObject selected;
        [Tooltip("The clickable Button (defaults to a Button on this object).")]
        [SerializeField] private Button button;

        /// <summary>The colour this swatch represents.</summary>
        public Color Color { get; private set; }

        private Action<WardrobeSwatch> _onClick;

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            if (button != null) button.onClick.AddListener(() => _onClick?.Invoke(this));
        }

        /// <summary>Tint the swatch and hook its click. Starts unselected.</summary>
        public void Bind(Color color, Action<WardrobeSwatch> onClick)
        {
            Color = color;
            _onClick = onClick;
            if (swatch != null) swatch.color = color;
            SetSelected(false);
        }

        public void SetSelected(bool on) { if (selected != null) selected.SetActive(on); }
    }
}
