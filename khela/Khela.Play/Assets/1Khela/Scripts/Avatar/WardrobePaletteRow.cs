using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace PlayCard.Avatar
{
    using ColorPalette = AvatarConfig.ColorPalette;

    /// <summary>
    /// A labelled row of colour swatches for one curated <see cref="AvatarConfig.ColorPalette"/> (e.g. Skin).
    /// <see cref="WardrobeShapeSliders"/> spawns it at the TOP of a shape tab; tapping a swatch calls
    /// <see cref="AvatarCreator.ApplyPalette"/> (recolours the target slot + any linked slots — Skin recolours
    /// Body AND Head) and moves the highlight. The chosen colour persists: it's stored as the target outfit's
    /// colour channel, which the avatar save serialises. Pure view — build the prefab with a label, a horizontal
    /// swatch container, and a swatch prefab.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobePaletteRow : MonoBehaviour
    {
        [Tooltip("Optional row label — set to the palette's target (e.g. \"Skin\").")]
        [SerializeField] private TMP_Text label;
        [Tooltip("Parent the swatches spawn under — give it a Horizontal Layout Group.")]
        [SerializeField] private Transform container;
        [Tooltip("The single-swatch prefab (has a WardrobeSwatch).")]
        [SerializeField] private WardrobeSwatch swatchPrefab;

        private readonly List<WardrobeSwatch> _swatches = new List<WardrobeSwatch>();
        private ColorPalette _palette;
        private AvatarCreator _creator;

        /// <summary>Fill the row for a palette. Highlights the swatch matching the avatar's current colour, if any.</summary>
        public void Bind(ColorPalette palette, AvatarCreator creator)
        {
            _palette = palette;
            _creator = creator;
            if (label != null) label.text = palette != null ? palette.target : "";
            Clear();
            if (palette?.swatches == null || swatchPrefab == null || container == null) return;

            Color? current = creator != null ? creator.CurrentPaletteColor(palette) : null;
            foreach (var c in palette.swatches)
            {
                var sw = Instantiate(swatchPrefab, container);
                sw.Bind(c, OnSwatch);
                if (current.HasValue && Approx(current.Value, c)) sw.SetSelected(true);
                _swatches.Add(sw);
            }
        }

        private void OnSwatch(WardrobeSwatch sw)
        {
            if (_creator != null && _palette != null) _creator.ApplyPalette(_palette, sw.Color);
            foreach (var s in _swatches) if (s != null) s.SetSelected(s == sw);
        }

        private static bool Approx(Color a, Color b)
            => Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f;

        private void Clear()
        {
            foreach (var s in _swatches) if (s != null) Destroy(s.gameObject);
            _swatches.Clear();
        }
    }
}
