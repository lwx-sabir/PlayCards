using System.Collections;
using Bozo.ModularCharacters;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Assembles the house DEALER avatar onto this table dealer's BoZo rig when the Table scene loads — the dealer
    /// equivalent of <see cref="WorldAvatarLoader"/>. Fetches the server's default (first) dealer (a house-only
    /// Character SKU flagged IsDealer, via <c>AvatarService.LoadDealerAsync</c>) and renders it read-only: no editing,
    /// no ownership — it's the house's look, shown to every player at the table.
    ///
    /// Put it on the dealer's BoZo rig (an <see cref="OutfitSystem"/>) placed at the table's "Dealer" spot. Falls back
    /// to a base if the house has no dealer yet or the scene wasn't launched from Boot (no session to authorize the fetch).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DealerAvatarLoader : MonoBehaviour
    {
        [Tooltip("BoZo's OutfitSystem on the dealer rig. Auto-found on this object/children if left empty.")]
        [SerializeField] private OutfitSystem outfitSystem;
        [Tooltip("Which dealer to load — its SKU id (e.g. \"dealer-female-1\"). Leave EMPTY for the default (first) dealer.")]
        [SerializeField] private string dealerId = "";
        [Tooltip("Fallback base if the dealer doesn't exist (or the scene wasn't launched from Boot, so the fetch can't authorize).")]
        [SerializeField] private string fallbackBaseId = "Base/1DefaultMale";

        private void Awake()
        {
            if (outfitSystem == null) outfitSystem = GetComponentInChildren<OutfitSystem>(true);
        }

        private IEnumerator Start()
        {
            if (outfitSystem == null)
            {
                Debug.LogWarning("[DealerAvatarLoader] no OutfitSystem on this dealer — put this on the BoZo dealer rig.");
                yield break;
            }
            // Wait until BoZo's rig has finished Init() before assembling parts (same as WorldAvatarLoader).
            while (outfitSystem != null && !outfitSystem.initalized) yield return null;
            LoadDealer();
        }

        /// <summary>Load the house dealer's look (or a fallback base) onto the rig.</summary>
        public async void LoadDealer()
        {
            if (outfitSystem == null) return;

            var dealer = AvatarService.Instance != null ? await AvatarService.Instance.LoadDealerAsync(dealerId) : null;

            CharacterData data = (dealer != null && !string.IsNullOrEmpty(dealer.BaseId))
                ? AvatarService.BuildCharacter(dealer)          // base + the dealer's authored look
                : AvatarService.LoadBaseData(fallbackBaseId);   // no dealer / no session → a base
            if (data == null) { Debug.LogWarning("[DealerAvatarLoader] nothing to load (no dealer + base missing?)."); return; }

            try { await BMAC_SaveSystem.LoadCharacter(outfitSystem, data); }
            catch (System.Exception e) { Debug.LogError($"[DealerAvatarLoader] load failed: {e.Message}"); }
        }
    }
}
