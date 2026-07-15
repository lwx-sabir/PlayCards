using System.Collections;
using Bozo.ModularCharacters;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Assembles the player's SAVED avatar (their selected cosmetics) onto this world character's BoZo rig when it
    /// spawns — the world equivalent of the wardrobe's load, with NO editing UI. Put it on the spawned world player
    /// (the Invector controller that wraps a BoZo <see cref="OutfitSystem"/> rig; e.g. the KHELA_CharacterBase rig).
    /// It waits for BoZo's rig to finish Init(), then loads <c>AvatarService.Mine</c> (the look the player saved in the
    /// wardrobe), falling back to a base if they have no saved avatar or the game wasn't launched from the Boot scene.
    ///
    /// Read-only: it renders the saved look; it never writes. Customisation stays in the Wardrobe.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldAvatarLoader : MonoBehaviour
    {
        [Tooltip("BoZo's OutfitSystem on this character. Auto-found on this object/children if left empty.")]
        [SerializeField] private OutfitSystem outfitSystem;
        [Tooltip("Fallback base if the player has no saved avatar yet (or the scene wasn't launched from Boot, so Mine is null).")]
        [SerializeField] private string fallbackBaseId = "Base/1DefaultMale";

        private void Awake()
        {
            if (outfitSystem == null) outfitSystem = GetComponentInChildren<OutfitSystem>(true);
        }

        private IEnumerator Start()
        {
            if (outfitSystem == null)
            {
                Debug.LogWarning("[WorldAvatarLoader] no OutfitSystem on this player — can't load cosmetics. Use the modular BoZo rig, not the baked prefab.");
                yield break;
            }
            // Wait until BoZo's rig has finished Init() (its own Awake sets `initalized`) before assembling parts.
            while (outfitSystem != null && !outfitSystem.initalized) yield return null;
            LoadMine();
        }

        /// <summary>Load the player's saved look (or a fallback base) onto the rig.</summary>
        public async void LoadMine()
        {
            if (outfitSystem == null) return;

            var mine = AvatarService.Instance != null
                ? (AvatarService.Instance.Mine ?? await AvatarService.Instance.LoadMineAsync())
                : null;   // no session (not launched from Boot) → fall back to a base

            CharacterData data = (mine != null && !string.IsNullOrEmpty(mine.BaseId))
                ? AvatarService.BuildCharacter(mine)            // base + saved diff (shapes/mods/outfits/colours)
                : AvatarService.LoadBaseData(fallbackBaseId);   // first-run / no session → a base

            if (data == null) { Debug.LogWarning("[WorldAvatarLoader] nothing to load (base missing?)."); return; }

            try { await BMAC_SaveSystem.LoadCharacter(outfitSystem, data); }
            catch (System.Exception e) { Debug.LogError($"[WorldAvatarLoader] load failed: {e.Message}"); }
        }
    }
}
