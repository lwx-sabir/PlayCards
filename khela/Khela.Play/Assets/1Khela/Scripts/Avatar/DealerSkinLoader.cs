using System.Collections;
using Bozo.ModularCharacters;
using PlayCard.Account;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Test-scene loader: waits for a session (device-guest auth from <see cref="AccountManager"/>) then assembles the
    /// server's default dealer look onto this rig's <see cref="OutfitSystem"/>. Fixes the "black rig" (no character =
    /// no runtime texture atlas). Works standalone (no Boot) as long as an <see cref="AccountManager"/> is in the scene
    /// — it device-guest signs in on its own Awake. Falls back to a base if there's no session/dealer, so the rig is
    /// never left black. Assign it to the dealer rig; leave <see cref="dealerId"/> empty for the default (first) dealer.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DealerSkinLoader : MonoBehaviour
    {
        [Tooltip("BoZo OutfitSystem on this rig. Auto-found on this object/children if empty.")]
        [SerializeField] private OutfitSystem outfitSystem;
        [Tooltip("Specific dealer SKU id (e.g. dealer-female-1). Empty = the default (first) dealer.")]
        [SerializeField] private string dealerId = "";
        [Tooltip("Fallback base if there's no session/dealer (so the rig is never left black).")]
        [SerializeField] private string fallbackBaseId = "Base/1DefaultMale";
        [Tooltip("Max seconds to wait for device-guest auth before giving up and using the fallback base.")]
        [SerializeField] private float authTimeout = 8f;

        private void Awake()
        {
            if (outfitSystem == null) outfitSystem = GetComponentInChildren<OutfitSystem>(true);
        }

        private IEnumerator Start()
        {
            if (outfitSystem == null) { Debug.LogWarning("[DealerSkinLoader] no OutfitSystem on this rig."); yield break; }
            while (outfitSystem != null && !outfitSystem.initalized) yield return null;

            // Wait for the session so the server fetch is authorized (AccountManager device-guest auths on its Awake).
            if (AccountManager.Instance != null)
            {
                float t = 0f;
                while (!AccountManager.Instance.IsReady && t < authTimeout) { t += Time.unscaledDeltaTime; yield return null; }
                if (!AccountManager.Instance.IsReady)
                    Debug.LogWarning("[DealerSkinLoader] auth not ready in time — using fallback base.");
            }
            else
            {
                Debug.LogWarning("[DealerSkinLoader] no AccountManager in the scene — add one for the server fetch; using fallback base.");
            }

            LoadDealer();
        }

        /// <summary>Load the server dealer (or a fallback base) onto the rig.</summary>
        public async void LoadDealer()
        {
            if (outfitSystem == null) return;

            var dealer = AvatarService.Instance != null ? await AvatarService.Instance.LoadDealerAsync(dealerId) : null;

            CharacterData data = (dealer != null && !string.IsNullOrEmpty(dealer.BaseId))
                ? AvatarService.BuildCharacter(dealer)          // the server dealer's authored look
                : AvatarService.LoadBaseData(fallbackBaseId);   // no session / no dealer → a base (never black)
            if (data == null) { Debug.LogWarning("[DealerSkinLoader] nothing to load (no dealer + base missing?)."); return; }

            try { await BMAC_SaveSystem.LoadCharacter(outfitSystem, data); }
            catch (System.Exception e) { Debug.LogError($"[DealerSkinLoader] load failed: {e.Message}"); }
        }
    }
}
