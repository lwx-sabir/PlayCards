using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Auto-generates the wardrobe's category rail from the <see cref="AvatarConfig"/>. The tabs ARE the config's
    /// <c>slots</c> (the curated dressing categories — Top/Bottom/Feet/HairFront/…): key = slot name, label =
    /// <see cref="AvatarConfig.SlotLabel"/>, icon = a BoZo thumbnail from that slot (via the config's OutfitIndex).
    /// Nothing to hand-fill — to change the tabs, edit the config's slot list (its Populate Starter Slots button).
    ///
    /// You supply TWO tab prefabs — the DEFAULT (unselected) look and the SELECTED (active) look. Per category it
    /// spawns one of each as adjacent siblings and shows only one, so selecting swaps which is visible. Single
    /// selection; fires <see cref="OnSelected"/> with the slot key.
    ///
    /// LAYOUT LIVES IN THE SCENE: put a Layout Group (+ ScrollRect if it overflows) on <see cref="container"/>; this
    /// script only instantiates + toggles, never sizes. Preview in edit mode via the context menu, or just Play.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeTabBar : MonoBehaviour
    {
        [Tooltip("Prefab for the DEFAULT (unselected) tab look (has a WardrobeTab).")]
        [SerializeField] private WardrobeTab defaultTabPrefab;
        [Tooltip("Prefab for the SELECTED (active) tab look (has a WardrobeTab).")]
        [SerializeField] private WardrobeTab selectedTabPrefab;
        [Tooltip("Parent the spawned tabs go under — the object with the Layout Group / scroll Content.")]
        [SerializeField] private Transform container;

        [Tooltip("The rules asset (Khela ▸ Avatar Config). Its `slots` list IS the set of tabs — icons come from its OutfitIndex.")]
        [SerializeField] private AvatarConfig config;

        [Tooltip("Show ONLY slots in this group (Face / Outfit / …). Empty = every slot. Set it per-panel so each group's " +
                 "rail shows just its own categories.")]
        [SerializeField] private string groupFilter = "";

        [Tooltip("Auto-select the first tab when built.")]
        [SerializeField] private bool selectFirst = true;

        [Header("Shape-tab icons (Body/Face/Shape — sliders have no BoZo thumbnail, assign your own)")]
        [SerializeField] private Sprite bodyIcon;
        [SerializeField] private Sprite faceIcon;
        [SerializeField] private Sprite eyeIcon;
        [SerializeField] private Sprite shapeIcon;

        [Header("Sets tab (full costumes — Outfit group)")]
        [Tooltip("Show a 'Sets' tab in the Outfit group that lists full costumes (set SKUs).")]
        [SerializeField] private bool showSetsTab = true;
        [SerializeField] private Sprite setsIcon;

        /// <summary>Tab key for the Sets tab — the grid shows set SKUs (full costumes) for this key.</summary>
        public const string SetsKey = "__sets";

        /// <summary>Fires with the selected tab's slot key whenever the selection changes.</summary>
        public event Action<string> OnSelected;

        /// <summary>The currently selected slot key (null before the first build).</summary>
        public string SelectedKey { get; private set; }

        // One category = a pair of spawned tabs (default + selected), one shown at a time.
        private sealed class Pair { public string key; public GameObject def; public GameObject sel; }
        private readonly List<Pair> _pairs = new List<Pair>();

        private void Start() => Build();

        /// <summary>Auto-build the rail from the config's slots. Clears any existing tabs first.</summary>
        public void Build()
        {
            Clear();
            if (defaultTabPrefab == null || selectedTabPrefab == null || container == null || config == null)
            {
                Debug.LogError("[WardrobeTabBar] assign Default Tab Prefab + Selected Tab Prefab + Container + Config.");
                return;
            }
            if (config.slots == null || config.slots.Count == 0)
            {
                Debug.LogWarning("[WardrobeTabBar] the config has no slots — run its “Populate Starter Slots” button.");
                return;
            }

            // SHAPE tabs first (sliders — Body / Face / Shape), then Sets (full costumes), then OUTFIT slot tabs.
            foreach (var st in ShapeTabs)
            {
                if (!InGroup(st.group)) continue;
                Sprite icon = st.category == "Body" ? bodyIcon : st.category == "Face" ? faceIcon : st.category == "Eyes" ? eyeIcon : shapeIcon;
                SpawnTab(ShapePrefix + st.category, st.label, icon);
            }
            if (showSetsTab && InGroup("Outfit")) SpawnTab(SetsKey, "Sets", setsIcon);
            foreach (var s in config.slots)
            {
                if (s == null || string.IsNullOrEmpty(s.slot)) continue;
                if (!InGroup(s.group)) continue;
                SpawnTab(s.slot, config.SlotLabel(s.slot), IconForSlot(s.slot));
            }

            // Select (not just highlight) the first tab so OnSelected fires — that's what tells the item grid / slider
            // panel to refresh AND swap which container shows. Without this, switching groups changes the tabs but not
            // the content below them.
            if (selectFirst && _pairs.Count > 0) Select(_pairs[0].key);
            else ApplySelection();
        }

        private bool InGroup(string group)
            => string.IsNullOrEmpty(groupFilter) || string.Equals(group, groupFilter, StringComparison.OrdinalIgnoreCase);

        // Spawn a tab pair (default + selected siblings, one shown at a time).
        private void SpawnTab(string key, string label, Sprite icon)
        {
            var def = Instantiate(defaultTabPrefab, container);
            def.Bind(key, label, icon, OnTabClicked);
            var sel = Instantiate(selectedTabPrefab, container);
            sel.Bind(key, label, icon, OnTabClicked);
            _pairs.Add(new Pair { key = key, def = def.gameObject, sel = sel.gameObject });
        }

        // ---- shape tabs (sliders, not items). Key = "shape:<Category>" so the content controller renders sliders. ----
        /// <summary>Tab-key prefix that marks a SHAPE tab (sliders). The suffix is the ShapeCategory (Body/BodyMod/Face).</summary>
        public const string ShapePrefix = "shape:";

        // BoZo's three shape categories, shown as sliders under the Body group: Body blends, Face blends, Body mods.
        private struct ShapeTab { public string category; public string label; public string group; }
        private static readonly ShapeTab[] ShapeTabs =
        {
            new ShapeTab { category = "Body",    label = "Body",  group = "Body" },
            new ShapeTab { category = "Face",    label = "Face",  group = "Body" },
            new ShapeTab { category = "Eyes",    label = "Eyes",  group = "Body" },
        };

        /// <summary>Refresh the rail to show only a group's slots — the group bar calls this on a group click. Empty = all.</summary>
        public void ShowGroup(string group)
        {
            groupFilter = group ?? "";
            Build();
        }

        /// <summary>Select a category by key: swaps which prefab shows and fires <see cref="OnSelected"/>.</summary>
        public void Select(string key)
        {
            SelectedKey = key;
            ApplySelection();
            OnSelected?.Invoke(key);
        }

        private void ApplySelection()
        {
            foreach (var p in _pairs)
            {
                bool on = p.key == SelectedKey;
                if (p.sel != null) p.sel.SetActive(on);
                if (p.def != null) p.def.SetActive(!on);
            }
        }

        private void OnTabClicked(WardrobeTab tab) => Select(tab.Key);

        /// <summary>A representative BoZo icon for a slot: the first indexed outfit in that slot that has an icon.</summary>
        private Sprite IconForSlot(string slot)
        {
            var idx = config != null ? config.outfitIndex : null;
            if (idx == null || idx.entries == null) return null;
            foreach (var e in idx.entries)
                if (e != null && e.icon != null && string.Equals(e.slot, slot, StringComparison.OrdinalIgnoreCase))
                    return e.icon;
            return null;
        }

        private void Clear()
        {
            foreach (var p in _pairs)
            {
                Destroy2(p.def);
                Destroy2(p.sel);
            }
            _pairs.Clear();
        }

        private static void Destroy2(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        // ---- editor preview (see the tabs without pressing Play; cleared on demand) ----
#if UNITY_EDITOR
        [ContextMenu("Preview tabs")]
        private void PreviewTabs()
        {
            Build();
            foreach (var p in _pairs)
            {
                if (p.def != null) p.def.hideFlags = HideFlags.DontSave;   // don't bake preview clones into the scene
                if (p.sel != null) p.sel.hideFlags = HideFlags.DontSave;
            }
        }

        [ContextMenu("Clear preview")]
        private void ClearPreview() => Clear();
#endif
    }
}
