using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Avatar
{
    using ShapeCategory = AvatarConfig.ShapeCategory;

    /// <summary>
    /// Auto-generates the body/face shape sliders for the selected shape tab (keys "shape:Body" / "shape:Face" /
    /// "shape:BodyMod"). Listens to the rail: on a shape tab it shows its panel and spawns one <see cref="WardrobeSliderItem"/>
    /// per editable shape in that category, each wired live to <see cref="AvatarCreator.GetNormalized"/> /
    /// <see cref="AvatarCreator.SetNormalized"/> (clamped to the curated limits). On any non-shape tab it hides its panel.
    ///
    /// The item grid and this panel are mutually exclusive (a tab is either items or sliders), so each toggles its own root.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeShapeSliders : MonoBehaviour
    {
        [Tooltip("The slider-row prefab (has a WardrobeSliderItem).")]
        [SerializeField] private WardrobeSliderItem sliderPrefab;
        [Tooltip("Parent the sliders spawn under — the slider panel's scroll Content.")]
        [SerializeField] private Transform container;
        [Tooltip("The whole slider panel — shown only while a shape tab is selected.")]
        [SerializeField] private GameObject panelRoot;
        [Tooltip("The rail whose selection drives this panel.")]
        [SerializeField] private WardrobeTabBar rail;
        [Tooltip("The creator whose shapes these sliders edit.")]
        [SerializeField] private AvatarCreator creator;
        [Tooltip("Colour-palette row prefab (has a WardrobePaletteRow). Shown at the TOP of tabs that have a palette (Body → Skin).")]
        [SerializeField] private WardrobePaletteRow paletteRowPrefab;

        private readonly List<WardrobeSliderItem> _items = new List<WardrobeSliderItem>();
        private readonly List<WardrobePaletteRow> _paletteRows = new List<WardrobePaletteRow>();

        private void OnEnable() { if (rail != null) rail.OnSelected += OnTab; }
        private void OnDisable() { if (rail != null) rail.OnSelected -= OnTab; }

        private void Start()
        {
            if (rail != null && !string.IsNullOrEmpty(rail.SelectedKey)) OnTab(rail.SelectedKey);
        }

        private void OnTab(string key)
        {
            bool isShape = !string.IsNullOrEmpty(key) && key.StartsWith(WardrobeTabBar.ShapePrefix);
            if (panelRoot != null) panelRoot.SetActive(isShape);
            Clear();
            if (!isShape) return;

            if (creator == null || sliderPrefab == null || container == null)
            {
                Debug.LogWarning($"[WardrobeShapeSliders] tab '{key}' but refs missing — creator={(creator != null)} prefab={(sliderPrefab != null)} container={(container != null)}. Assign them on the component.");
                return;
            }

            string cat = key.Substring(WardrobeTabBar.ShapePrefix.Length);   // "Body" / "Face" / "BodyMod"
            if (!Enum.TryParse<ShapeCategory>(cat, ignoreCase: true, out var category))
            {
                Debug.LogWarning($"[WardrobeShapeSliders] can't parse shape category from '{key}'.");
                return;
            }

            // Colour palette row(s) FIRST — e.g. the Body tab leads with the Skin palette.
            SpawnPalettes(category);

            var shapes = creator.EditableShapes(category);
            Debug.Log($"[WardrobeShapeSliders] {key} → category={category}, gender={creator.CurrentGender}, {shapes.Count} sliders.");

            foreach (var s in shapes)
            {
                if (s == null) continue;
                var shape = s;   // capture per-iteration for the closures
                var row = Instantiate(sliderPrefab, container);
                row.Bind(string.IsNullOrEmpty(shape.label) ? shape.key : shape.label,
                         creator.GetNormalized(shape),
                         v => creator.SetNormalized(shape, v));
                _items.Add(row);
            }
        }

        // Which curated palette (by target) leads a shape tab, or null for none. Body → Skin; extend as needed (e.g. Eyes → Eyes).
        private static string PaletteTargetFor(ShapeCategory category)
            => category == ShapeCategory.Body ? "Skin" : null;

        /// <summary>Spawn the tab's colour palette row at the top of the list (before the sliders).</summary>
        private void SpawnPalettes(ShapeCategory category)
        {
            string target = PaletteTargetFor(category);
            if (target == null || paletteRowPrefab == null || creator == null) return;
            var pal = creator.Palettes?.Find(p => p != null && string.Equals(p.target, target, StringComparison.OrdinalIgnoreCase));
            if (pal == null) return;
            var row = Instantiate(paletteRowPrefab, container);
            row.transform.SetAsFirstSibling();   // keep it above the sliders even if spawn order shifts
            row.Bind(pal, creator);
            _paletteRows.Add(row);
        }

        private void Clear()
        {
            foreach (var i in _items) if (i != null) Destroy(i.gameObject);
            _items.Clear();
            foreach (var r in _paletteRows) if (r != null) Destroy(r.gameObject);
            _paletteRows.Clear();
        }
    }
}
