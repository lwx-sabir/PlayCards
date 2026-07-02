using System;
using System.Threading.Tasks;
using Bozo.ModularCharacters;
using PlayCard.Home;
using UnityEngine;

namespace PlayCard.Avatar
{
    using Gender = AvatarConfig.Gender;
    using BaseChoice = AvatarConfig.BaseChoice;

    /// <summary>
    /// One character in the onboarding <see cref="PlayCard.Home.CarouselController"/> ring. It IS a BoZo actor (has an
    /// <see cref="OutfitSystem"/>); the carousel positions/scales it and calls <see cref="SetSelected"/> on the centred
    /// one. Implements <see cref="ICarouselItem"/> so it reuses the exact ring + swipe logic the Home/Lobby carousels use.
    ///
    /// IMPORTANT: BoZo's per-character MERGE is expensive — doing it for the whole ring at once freezes the app. So each
    /// actor is forced to <c>LoadMode.Manual</c> (never auto-merges in Awake) and its base is merged on demand, ONE at a
    /// time, only when it becomes the centred character (<see cref="EnsureLoadedAsync"/>). A <see cref="Prebaked"/> item
    /// (built from a ready display prefab) skips merging entirely.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarCarouselItem : MonoBehaviour, ICarouselItem
    {
        [SerializeField] private string baseId;            // Resources path, e.g. "Base/Mary"
        [SerializeField] private string displayName;
        [SerializeField] private Gender gender;
        [Tooltip("The BSMC actor's OutfitSystem. Auto-found on this object/children if empty.")]
        [SerializeField] private OutfitSystem outfitSystem;
        [Tooltip("Built from a ready display prefab — already looks right, so it is NEVER merged at runtime.")]
        [SerializeField] private bool prebaked;
        [Tooltip("Optional: shown only while this item is the centred (selected) one — a ring, glow, nameplate, etc.")]
        [SerializeField] private GameObject selectedVisual;

        private bool _loaded;

        public string BaseId => baseId;
        public string DisplayNameText => displayName;
        public Gender Gender => gender;

        /// <summary>True once this item's base is merged onto it (or it was prebaked) — so it's never merged twice.</summary>
        public bool IsLoaded => prebaked || _loaded;

        // ---- ICarouselItem ----
        public Transform Transform => transform;
        public void SetSelected(bool selected)
        {
            if (selectedVisual != null) selectedVisual.SetActive(selected);
        }

        private void Awake()
        {
            if (outfitSystem == null) outfitSystem = GetComponentInChildren<OutfitSystem>(true);
        }

        /// <summary>Author-time: stamp this item with a roster entry (called by the builder). Points the actor's
        /// OutfitSystem at the base and FORCES manual load so it never auto-merges in Awake (the freeze cause).</summary>
        public void Configure(BaseChoice choice, bool prebakedItem)
        {
            if (choice == null) return;
            baseId = choice.id;
            displayName = choice.displayName;
            gender = choice.gender;
            prebaked = prebakedItem;
            if (outfitSystem == null) outfitSystem = GetComponentInChildren<OutfitSystem>(true);
            if (outfitSystem != null)
            {
                outfitSystem.characterData = Resources.Load<CharacterObject>(baseId);
                outfitSystem.loadMode = OutfitSystem.LoadMode.Manual;   // NEVER auto-merge — the carousel loads on demand
            }
        }

        /// <summary>Runtime: merge this item's base onto its actor (copy-safe). No-op if prebaked or already loaded.
        /// Callers must serialise these (one merge at a time) — a whole ring merging at once freezes the app.</summary>
        public async Task EnsureLoadedAsync()
        {
            if (IsLoaded || outfitSystem == null || string.IsNullOrEmpty(baseId)) return;
            var data = AvatarService.LoadBaseData(baseId);
            if (data == null) return;

            outfitSystem.async = true;   // spread the merge (Resources.LoadAsync + yields) over frames — far less stutter
            _loaded = true;              // guard re-entry before the await
            try { await BMAC_SaveSystem.LoadCharacter(outfitSystem, data); }
            catch (Exception e) { _loaded = false; Debug.LogError($"[AvatarCarouselItem] load '{baseId}' failed: {e.Message}"); }
        }
    }
}
