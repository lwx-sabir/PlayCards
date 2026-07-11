using System.Collections;
using Bozo.ModularCharacters;
using PlayCard.App;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Bridges BoZo's built-in character creator to OUR system. Drop it on BoZo's <see cref="OutfitSystem"/> actor in a
    /// duplicated creator scene. BoZo's CharacterCreator keeps driving ALL the editing UI (shapes, outfits, colours);
    /// this only:
    ///  • loads the player's SAVED avatar onto the rig when the scene opens (so it resumes their look), and
    ///  • on Save, snapshots the rig → our <see cref="AvatarData"/> → server → Home.
    ///
    /// Wire a Save button's OnClick → <see cref="SaveAndExit"/> and a Back button → <see cref="Exit"/>. The server
    /// re-sanitizes on write, so it stays the guardrail even though BoZo's creator itself is unconstrained. (Curation —
    /// hiding the gender axis / clamping sliders — can be layered on later by trimming BoZo's UI.)
    ///
    /// Test from Boot: AvatarService/AccountManager come from the Boot scene (DontDestroyOnLoad); opening the Wardrobe
    /// scene directly leaves Mine null and it falls back to a base.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeBridge : MonoBehaviour
    {
        [Tooltip("BoZo's live character. Auto-found on this object/children if empty.")]
        [SerializeField] private OutfitSystem outfitSystem;
        [Tooltip("BoZo's creator UI (the CharacterCreator---… object). Optional — used to resync its sliders after LoadMine.")]
        [SerializeField] private CharacterCreator creator;
        [Tooltip("Load the player's saved avatar when BoZo's rig has finished initialising.")]
        [SerializeField] private bool loadMineOnStart = true;
        [SerializeField] private string fallbackBaseId = "Base/1DefaultMale";

        private string _gender = "Male";
        private string _baseId;
        private bool _busy;

        private void Awake()
        {
            if (outfitSystem == null) outfitSystem = GetComponentInChildren<OutfitSystem>(true);
        }

        private IEnumerator Start()
        {
            if (!loadMineOnStart) yield break;
            // Wait until BoZo's rig has finished Init() (runs in its own Awake → sets `initalized`). No fixed delay to
            // tune; and with the actor's Character Data cleared there's no competing Default load, so no visible flash.
            while (outfitSystem != null && !outfitSystem.initalized) yield return null;
            LoadMine();
        }

        /// <summary>Load the player's saved avatar (or a fallback base) onto BoZo's rig.</summary>
        public async void LoadMine()
        {
            if (outfitSystem == null) { Debug.LogWarning("[WardrobeBridge] no OutfitSystem."); return; }

            var mine = AvatarService.Instance.Mine ?? await AvatarService.Instance.LoadMineAsync();
            CharacterData data;
            if (mine != null && !string.IsNullOrEmpty(mine.BaseId))
            {
                _gender = string.IsNullOrEmpty(mine.Gender) ? "Male" : mine.Gender;
                _baseId = mine.BaseId;
                data = AvatarService.BuildCharacter(mine);            // base + saved diff (shapes/mods/outfits)
            }
            else
            {
                _baseId = fallbackBaseId;
                data = AvatarService.LoadBaseData(fallbackBaseId);
            }
            if (data == null) { Debug.LogWarning("[WardrobeBridge] nothing to load."); return; }

            try
            {
                await BMAC_SaveSystem.LoadCharacter(outfitSystem, data);
                if (creator != null) { creator.GetBodyBlends(); creator.GetBodyMods(); }   // resync BoZo's sliders to the loaded look
            }
            catch (System.Exception e) { Debug.LogError($"[WardrobeBridge] load failed: {e.Message}"); }
        }

        /// <summary>Snapshot the current look, push it to the server (which re-sanitizes), and go Home.</summary>
        public async void SaveAndExit()
        {
            if (_busy || outfitSystem == null) return;
            _busy = true;

            var data = BMAC_SaveSystem.GetCharacterData(outfitSystem);
            var avatar = AvatarMapper.FromCharacter(data, _gender, _baseId);
            bool ok = await AvatarService.Instance.SaveAsync(avatar);

            if (ok) { SceneNavigator.GoToHome(); return; }   // Home re-renders via AvatarService.MineChanged
            Debug.LogWarning("[WardrobeBridge] save failed — staying open.");
            _busy = false;
        }

        /// <summary>Discard edits and go Home (nothing persisted until Save).</summary>
        public void Exit() => SceneNavigator.GoToHome();
    }
}
