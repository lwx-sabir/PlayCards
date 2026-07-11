using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    /// <summary>
    /// The wardrobe's top group tabs (Body / Face / Outfit) — hand-placed buttons, not generated. You drag in each
    /// group's Button plus the objects that show its SELECTED look (e.g. Icon_Focus, FocusTab). Clicking a group turns
    /// on its focus objects (and off the others') and refreshes the one <see cref="WardrobeTabBar"/> rail to that
    /// group's slots. No prefabs, no spawning — just wiring what's already in the scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeGroupBar : MonoBehaviour
    {
        [Serializable]
        public sealed class Group
        {
            [Tooltip("Group key — must match the slots' group in the config (Body / Face / Outfit).")]
            public string key;
            [Tooltip("The group's button (already in the scene).")]
            public Button button;
            [Tooltip("Objects shown ONLY while this group is selected — e.g. Icon_Focus and FocusTab.")]
            public GameObject[] focus;
        }

        [Tooltip("The three groups, in order. Drag each button + its focus objects.")]
        [SerializeField] private List<Group> groups = new List<Group>();

        [Tooltip("The slot rail this bar drives — a group click refreshes it to that group's slots.")]
        [SerializeField] private WardrobeTabBar rail;

        [Tooltip("Auto-select the first group on start.")]
        [SerializeField] private bool selectFirst = true;

        /// <summary>Fires with the selected group's key whenever it changes.</summary>
        public event Action<string> OnSelected;

        /// <summary>The currently selected group key (null before the first selection).</summary>
        public string SelectedKey { get; private set; }

        private void Start()
        {
            foreach (var g in groups)
            {
                if (g?.button == null) continue;
                string key = g.key;                          // capture per-iteration for the listener
                g.button.onClick.AddListener(() => Select(key));
            }
            if (selectFirst && groups.Count > 0) Select(groups[0].key);
        }

        /// <summary>Select a group: lights its focus objects (dims the others') and refreshes the rail to its slots.</summary>
        public void Select(string key)
        {
            SelectedKey = key;
            foreach (var g in groups)
            {
                if (g == null) continue;
                bool on = g.key == key;
                if (g.focus != null)
                    foreach (var f in g.focus)
                        if (f != null) f.SetActive(on);
            }
            if (rail != null) rail.ShowGroup(key);
            OnSelected?.Invoke(key);
        }
    }
}
