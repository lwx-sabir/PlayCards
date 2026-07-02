using System;
using System.Threading.Tasks;
using PlayCard.Home;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Drives the onboarding character ring: reuses the Home/Lobby <see cref="CarouselController"/> (put this on the SAME
    /// GameObject) and fills it with one <see cref="AvatarCarouselItem"/> per <see cref="AvatarConfig"/> roster entry.
    ///
    /// BoZo's merge is expensive, so this NEVER merges the whole ring. If a roster entry has a display prefab it's shown
    /// as-is (no merge). Otherwise the generic actor prefab is used and its base is merged ONLY when it becomes the
    /// centred character, ONE at a time (<see cref="loadBasesAtRuntime"/>). Merging all of them at once freezes the app.
    ///
    /// Author-time: assign a BSMC actor prefab + the config, then click <b>Build from AvatarConfig</b>.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CarouselController))]
    public sealed class AvatarCarousel : MonoBehaviour
    {
        [Tooltip("The rules/roster asset (Khela ▸ Avatar Config).")]
        [SerializeField] private AvatarConfig config;
        [Tooltip("Fallback BSMC actor prefab (has an OutfitSystem) for roster entries WITHOUT a display prefab.")]
        [SerializeField] private GameObject actorPrefab;
        [Tooltip("Merge the centred character's base at runtime (one at a time). Turn OFF if you use only baked display " +
                 "prefabs — then nothing is ever merged.")]
        [SerializeField] private bool loadBasesAtRuntime = true;
        [Tooltip("After the centred one, keep merging the OTHER characters in the background (one at a time) so a later " +
                 "swipe never lands on an un-merged body. Off = merge each only when first centred.")]
        [SerializeField] private bool preloadAllAtStart = true;

        /// <summary>Fires with the newly-centred character on every selection change.</summary>
        public event Action<AvatarCarouselItem> OnSelected;

        /// <summary>The currently centred character (null until the ring initialises).</summary>
        public AvatarCarouselItem Selected { get; private set; }

        /// <summary>True once every character is merged/ready (or all are prebaked). A loading overlay hides until this.</summary>
        public bool AllPreloaded { get; private set; }

        /// <summary>Fires once when <see cref="AllPreloaded"/> flips true.</summary>
        public event Action OnAllPreloaded;

        public AvatarConfig Config => config;

        private CarouselController _carousel;
        private AvatarCarouselItem[] _all;
        private bool _mergeBusy;

        private void Awake() => _carousel = GetComponent<CarouselController>();

        private void Start()
        {
            _all = GetComponentsInChildren<AvatarCarouselItem>(true);
            if (_carousel != null)
            {
                _carousel.OnSelectionChanged += HandleSelection;
                _carousel.Rebuild();   // sets the initial selection (which kicks the merge pump: centred first)
            }
            PumpMerges();              // start the background preload even if the selection didn't change
            MarkPreloadedIfDone();     // baked / LoadBasesAtRuntime=false → nothing to load, ready immediately
        }

        private void OnDestroy()
        {
            if (_carousel != null) _carousel.OnSelectionChanged -= HandleSelection;
        }

        private void HandleSelection(ICarouselItem item)
        {
            Selected = item as AvatarCarouselItem;
            OnSelected?.Invoke(Selected);
            if (Application.isPlaying) PumpMerges();
        }

        // ONE merge at a time — concurrent merges freeze the app. Always loads the CENTRED character first (ready fast),
        // then, if preloading, keeps merging the rest in the background so a later swipe never lands on a naked body.
        private async void PumpMerges()
        {
            if (!loadBasesAtRuntime || _mergeBusy) return;
            _mergeBusy = true;
            try
            {
                int guard = 0;
                while (guard++ < 256)
                {
                    var next = (Selected != null && !Selected.IsLoaded)
                        ? Selected
                        : (preloadAllAtStart ? FirstUnloaded() : null);
                    if (next == null) break;
                    await next.EnsureLoadedAsync();
                    // Breathe: let a couple of frames render before the next heavy merge so the screen never locks up.
                    await Task.Yield();
                    await Task.Yield();
                }
            }
            finally { _mergeBusy = false; }
            MarkPreloadedIfDone();
        }

        private void MarkPreloadedIfDone()
        {
            if (AllPreloaded) return;
            if (!loadBasesAtRuntime || FirstUnloaded() == null)
            {
                AllPreloaded = true;
                OnAllPreloaded?.Invoke();
            }
        }

        private AvatarCarouselItem FirstUnloaded()
        {
            if (_all == null) return null;
            foreach (var it in _all) if (it != null && !it.IsLoaded) return it;
            return null;
        }

#if UNITY_EDITOR
        /// <summary>Editor: (re)build the ring from the config roster — a display prefab per character if set, else the
        /// generic actor prefab.</summary>
        public void BuildFromConfig()
        {
            if (config == null)
            {
                Debug.LogWarning("[AvatarCarousel] Assign the AvatarConfig first.");
                return;
            }
            ClearItems();

            foreach (var choice in config.roster)
            {
                bool prebaked = choice.displayPrefab != null;
                var prefab = prebaked ? choice.displayPrefab : actorPrefab;
                if (prefab == null)
                {
                    Debug.LogWarning($"[AvatarCarousel] '{choice.id}' has no display prefab and no fallback Actor Prefab — skipped.");
                    continue;
                }

                var go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, transform);
                if (go == null) continue;
                UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Build Avatar Carousel");
                go.name = "Avatar_" + (string.IsNullOrEmpty(choice.displayName) ? choice.id : choice.displayName);

                var item = go.GetComponent<AvatarCarouselItem>() ?? go.AddComponent<AvatarCarouselItem>();
                item.Configure(choice, prebaked);
                UnityEditor.EditorUtility.SetDirty(go);
            }

            var cc = GetComponent<CarouselController>();
            if (cc != null) cc.Rebuild();
            UnityEditor.EditorUtility.SetDirty(this);
        }

        /// <summary>Editor: remove the spawned character items (leaves any other children alone).</summary>
        public void ClearItems()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                if (child.GetComponent<AvatarCarouselItem>() != null)
                    UnityEditor.Undo.DestroyObjectImmediate(child);
            }
        }
#endif
    }

#if UNITY_EDITOR
    [UnityEditor.CustomEditor(typeof(AvatarCarousel))]
    public sealed class AvatarCarouselEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var c = (AvatarCarousel)target;
            UnityEditor.EditorGUILayout.HelpBox(
                "Assign Config (+ a fallback BSMC actor prefab), then Build from AvatarConfig. Give roster entries a " +
                "display prefab for distinct, in-editor, zero-merge characters. Without one, the centred character is " +
                "merged at runtime (one at a time). Re-run Build after editing the roster or updating this script.",
                UnityEditor.MessageType.Info);
            if (GUILayout.Button("Build from AvatarConfig")) c.BuildFromConfig();
            if (GUILayout.Button("Clear characters")) c.ClearItems();
            UnityEditor.EditorGUILayout.Space();
            DrawDefaultInspector();
        }
    }
#endif
}
