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

        private readonly List<WardrobeSliderItem> _items = new List<WardrobeSliderItem>();

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

        private void Clear()
        {
            foreach (var i in _items) if (i != null) Destroy(i.gameObject);
            _items.Clear();
        }
    }
}
