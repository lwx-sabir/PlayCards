using UnityEngine;
using UnityEngine.Rendering;

namespace PlayCard.Quality
{
    /// <summary>
    /// Drives the post-processing look per graphics tier (Low / Mid / High), using a TWO-LAYER volume stack:
    ///
    ///   • BASE volume    (priority 0)  — the shared look every tier gets (<see cref="sharedBase"/>).
    ///   • OVERRIDE volume(priority 10) — a SPARSE per-tier profile layered on top.
    ///
    /// URP merges volumes PER PARAMETER, not per component: <c>VolumeComponent.Override</c> guards every write
    /// with <c>if (toParam.overrideState)</c>, so a parameter left un-ticked in the override profile is skipped
    /// entirely and inherits the base value. That means a tier override that ticks only <c>Bloom.intensity</c>
    /// keeps every other Bloom setting — and every other effect — from the base. Authoring deltas, not copies.
    ///
    /// Extra Volumes cost NO extra render pass: the volume framework is a CPU-side fold that resolves to one
    /// stack, and URP's Uber post pass runs once regardless. The only real GPU cost is when an override ADDS a
    /// component the base lacks (e.g. Depth of Field) — that switches on actual work, so do it deliberately.
    ///
    /// Tier resolution order (first hit wins):
    ///   1. A player's explicit choice saved in PlayerPrefs (a settings menu calling <see cref="SetTier"/>).
    ///   2. <see cref="mode"/>: <c>DefaultLow</c> = always Low; <c>AutoDetect</c> = pick from device capacity.
    /// Auto-detect is deliberately CONSERVATIVE — it only promotes above Low on clearly capable hardware, so a
    /// misread device fails safe to the cheapest tier.
    ///
    /// BACKWARD COMPATIBLE: leave <see cref="sharedBase"/> and all three override slots empty and this behaves
    /// exactly as before — the Low/Mid/High full profiles drive the base volume and no second volume is created.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PostFxTierController : MonoBehaviour
    {
        public enum GraphicsTier { Low = 0, Mid = 1, High = 2 }
        public enum SelectionMode { DefaultLow, AutoDetect }

        /// <summary>Why <see cref="CurrentTier"/> ended up where it did — surfaced in the Inspector.</summary>
        public enum TierSource { DefaultLow, AutoDetect, SavedPref, ExplicitCall, EditorPreview }

        [Header("Target")]
        [Tooltip("The base Global Volume whose profile is swapped. If left empty, a Volume on THIS GameObject is used.")]
        [SerializeField] private Volume volume;

        [Header("Base look (recommended)")]
        [Tooltip("The ONE shared grade used by every tier. When assigned, the Low/Mid/High full profiles below " +
                 "are ignored as the base and tiers differ only via their override profiles.")]
        [SerializeField] private VolumeProfile sharedBase;

        [Header("Full per-tier profiles (legacy — used only when Base look is empty)")]
        [SerializeField] private VolumeProfile low;
        [SerializeField] private VolumeProfile mid;
        [SerializeField] private VolumeProfile high;

        [Header("Per-tier overrides (only TICKED parameters apply)")]
        [Tooltip("Second, higher-priority Global Volume. Leave empty to have one auto-created at runtime — but " +
                 "assign an in-scene one if you want to author/preview overrides in Edit mode.")]
        [SerializeField] private Volume overrideVolume;
        [Tooltip("Must be strictly GREATER than the base volume's priority, or the override is applied first and loses.")]
        [SerializeField] private float overridePriority = 10f;
        [SerializeField] private VolumeProfile lowOverride;
        [SerializeField] private VolumeProfile midOverride;
        [SerializeField] private VolumeProfile highOverride;

        [Header("Behaviour")]
        [Tooltip("How the tier is chosen when the player hasn't picked one manually.")]
        [SerializeField] private SelectionMode mode = SelectionMode.DefaultLow;

#if UNITY_EDITOR
        [Header("Editor preview")]
        [Tooltip("Play mode only: change this to A/B the tiers live. Editor-only — not in builds, and it " +
                 "does NOT persist to PlayerPrefs, so it can't poison the real tier resolution.")]
        [SerializeField] private GraphicsTier previewTier = GraphicsTier.Low;
#endif

        private const string PrefKey = "khela.gfxTier";
        private const string OverrideVolumeName = "PostFX_TierOverride";

        /// <summary>The tier currently applied.</summary>
        public GraphicsTier CurrentTier { get; private set; } = GraphicsTier.Low;
        /// <summary>Why <see cref="CurrentTier"/> was chosen.</summary>
        public TierSource ResolvedFrom { get; private set; } = TierSource.DefaultLow;
        /// <summary>The profile actually assigned to the base volume.</summary>
        public VolumeProfile ActiveBaseProfile { get; private set; }
        /// <summary>The profile actually assigned to the override volume, or null when the tier is base-only.</summary>
        public VolumeProfile ActiveOverrideProfile { get; private set; }
        /// <summary>True when an override profile is live and contributing.</summary>
        public bool OverrideActive => ActiveOverrideProfile != null && overrideVolume != null && overrideVolume.weight > 0f;

        public Volume BaseVolume => volume;
        public Volume TierOverrideVolume => overrideVolume;

        private void Reset()
        {
            // Convenience in-editor: grab a Volume already on this object, plus a hand-made override child.
            volume = GetComponent<Volume>();
            var existing = transform.Find(OverrideVolumeName);
            if (existing != null) overrideVolume = existing.GetComponent<Volume>();
        }

        private void Awake()
        {
            if (volume == null) volume = GetComponent<Volume>();
            var tier = ResolveTier();
#if UNITY_EDITOR
            previewTier = tier;   // start the preview dropdown in sync with what actually resolved
#endif
            Apply(tier, allowCreate: true);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only: applies <see cref="previewTier"/> the moment it changes in the Inspector during Play,
        /// so tiers can be A/B'd live. Deliberately does NOT persist — see <see cref="SetTier"/> for that.
        /// Never creates GameObjects (Unity forbids that from OnValidate), so it only drives an override volume
        /// that already exists.
        /// </summary>
        private void OnValidate()
        {
            if (!Application.isPlaying || volume == null) return;
            ResolvedFrom = TierSource.EditorPreview;
            Apply(previewTier, allowCreate: false);
        }
#endif

        /// <summary>
        /// Set the tier explicitly (e.g. from a graphics-settings dropdown). Persists the choice by default so
        /// it survives relaunches and applies in every scene that hosts a controller.
        /// </summary>
        public void SetTier(GraphicsTier tier, bool persist = true)
        {
            if (persist)
            {
                PlayerPrefs.SetInt(PrefKey, (int)tier);
                PlayerPrefs.Save();
            }
            ResolvedFrom = TierSource.ExplicitCall;
            Apply(tier, allowCreate: true);
        }

        /// <summary>Clear any saved override and fall back to <see cref="mode"/> (Low or auto-detect).</summary>
        public void ClearSavedTier()
        {
            PlayerPrefs.DeleteKey(PrefKey);
            Apply(ResolveTier(), allowCreate: true);
        }

        private GraphicsTier ResolveTier()
        {
            if (PlayerPrefs.HasKey(PrefKey))
            {
                ResolvedFrom = TierSource.SavedPref;
                return (GraphicsTier)Mathf.Clamp(PlayerPrefs.GetInt(PrefKey), 0, 2);
            }

            if (mode == SelectionMode.AutoDetect)
            {
                ResolvedFrom = TierSource.AutoDetect;
                return DetectTier();
            }

            ResolvedFrom = TierSource.DefaultLow;
            return GraphicsTier.Low;
        }

        /// <summary>
        /// Conservative device-capacity heuristic. RAM + VRAM + core count must all clear a bar to promote a
        /// tier; anything unknown or below bar stays on Low. Tune the thresholds against real target devices.
        /// </summary>
        private static GraphicsTier DetectTier()
        {
            int ramMB  = SystemInfo.systemMemorySize;    // total system RAM
            int vramMB = SystemInfo.graphicsMemorySize;  // reported GPU memory (approx on mobile)
            int cores  = SystemInfo.processorCount;

            if (ramMB >= 6000 && vramMB >= 2000 && cores >= 8) return GraphicsTier.High;
            if (ramMB >= 4000 && vramMB >= 1500 && cores >= 6) return GraphicsTier.Mid;
            return GraphicsTier.Low;
        }

        /// <summary>Base profile for a tier: the shared base if set, else the legacy per-tier full profile.</summary>
        private VolumeProfile ResolveBaseProfile(GraphicsTier tier)
        {
            if (sharedBase != null) return sharedBase;

            VolumeProfile p = tier switch
            {
                GraphicsTier.High => high,
                GraphicsTier.Mid  => mid,
                _                 => low,
            };
            // Fail safe: if the requested tier's profile isn't wired, fall back to the lowest one that is.
            return p != null ? p : (low != null ? low : (mid != null ? mid : high));
        }

        /// <summary>
        /// Override profile for a tier. Deliberately has NO fallback chain — a missing override must mean
        /// "base only", never "silently borrow another tier's overrides".
        /// </summary>
        private VolumeProfile ResolveOverrideProfile(GraphicsTier tier) => tier switch
        {
            GraphicsTier.High => highOverride,
            GraphicsTier.Mid  => midOverride,
            _                 => lowOverride,
        };

        /// <summary>
        /// Makes sure a usable override volume exists and is configured. Returns false (zero cost, nothing
        /// created) when no override profiles are wired at all.
        /// </summary>
        private bool EnsureOverrideVolume(bool allowCreate)
        {
            bool anySlotWired = lowOverride != null || midOverride != null || highOverride != null;
            if (!anySlotWired && overrideVolume == null) return false;

            if (overrideVolume == null)
            {
                if (!allowCreate) return false;
                var go = new GameObject(OverrideVolumeName);
                go.transform.SetParent(transform, false);
                // Must share this object's layer — the camera's Volume Mask filters by layer.
                go.layer = gameObject.layer;
                overrideVolume = go.AddComponent<Volume>();
            }

            float basePriority = volume != null ? volume.priority : 0f;
            if (overridePriority <= basePriority)
            {
                Debug.LogWarning(
                    $"[PostFxTierController] overridePriority ({overridePriority}) must be greater than the base " +
                    $"volume priority ({basePriority}) or the override is applied first and loses. " +
                    $"Clamping to {basePriority + 1f}.", this);
                overridePriority = basePriority + 1f;
            }

            overrideVolume.isGlobal = true;
            overrideVolume.blendDistance = 0f;
            overrideVolume.priority = overridePriority;
            return true;
        }

        private void Apply(GraphicsTier tier, bool allowCreate)
        {
            if (volume == null)
            {
                Debug.LogWarning("[PostFxTierController] No Volume assigned; cannot apply post-processing tier.", this);
                return;
            }

            var baseProfile = ResolveBaseProfile(tier);
            if (baseProfile == null)
            {
                Debug.LogWarning("[PostFxTierController] No base profile assigned; leaving volume untouched.", this);
                return;
            }

            // Only ever assign sharedProfile. Reading Volume.profile deep-copies the asset on the GETTER, leaks
            // that copy, and permanently shadows sharedProfile — after which tier switching silently stops working.
            volume.sharedProfile = baseProfile;
            ActiveBaseProfile = baseProfile;

            var overrideProfile = ResolveOverrideProfile(tier);
            if (EnsureOverrideVolume(allowCreate) && overrideVolume != null)
            {
                overrideVolume.sharedProfile = overrideProfile;
                // weight 0 and a null profile are both early-outs in VolumeManager.Update — cheaper than
                // toggling the component, and it avoids register/unregister churn.
                overrideVolume.weight = overrideProfile != null ? 1f : 0f;
            }

            ActiveOverrideProfile = overrideProfile;
            CurrentTier = tier;
        }

        /// <summary>One-line diagnostic of what is actually live. Safe to call from a QA build.</summary>
        public string DescribeResolvedState()
        {
            string base_ = ActiveBaseProfile != null ? ActiveBaseProfile.name : "(none)";
            string ovr = ActiveOverrideProfile != null
                ? $"{ActiveOverrideProfile.name} ({CountOverriddenParameters(ActiveOverrideProfile)} params)"
                : "(none - base only)";
            return $"tier={CurrentTier} (from {ResolvedFrom}) | base={base_} | override={ovr}";
        }

        /// <summary>
        /// How many parameters a profile actually overrides. Components with <c>active == false</c> are skipped
        /// wholesale (URP does the same), so this matches what the volume stack will really apply.
        /// </summary>
        public static int CountOverriddenParameters(VolumeProfile profile)
        {
            if (profile == null) return 0;
            int n = 0;
            foreach (var c in profile.components)
            {
                if (c == null || !c.active) continue;
                foreach (var p in c.parameters)
                    if (p != null && p.overrideState) n++;
            }
            return n;
        }
    }
}
