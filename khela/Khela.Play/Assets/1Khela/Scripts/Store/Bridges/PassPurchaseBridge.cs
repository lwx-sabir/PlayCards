using PlayCard.Pass;
using UnityEngine;

namespace PlayCard.Store.Bridges
{
    /// <summary>
    /// Wires the pass popup to the store: <c>PassPanel.SubscribeRequested</c> → buy the golden pass (the catalog's
    /// GoldenPass product) through <see cref="IapService"/>; the server verifies the subscription receipt and opens the
    /// golden window (<c>PassService.GrantGoldenAsync</c>); on any outcome the pass state is refreshed so the screen shows
    /// the truth. Sits beside <c>PassPanel</c>; the reference auto-finds on the same object when left empty.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PassPurchaseBridge : MonoBehaviour
    {
        [SerializeField] private PassPanel panel;
        [Tooltip("OPTIONAL. When set, a subscribe request OPENS THE PITCH instead of going straight to the store — " +
                 "the popup then asks for the purchase itself. Leave both this and the prefab empty and a tap buys " +
                 "immediately, which is the old behaviour.")]
        [SerializeField] private PassPromoPanel promo;
        [Tooltip("The pitch as a PREFAB, spawned the first time it is needed and kept alive after. Use this when the " +
                 "popup does not live in the pass canvas — it is its own screen and can be opened from anywhere.")]
        [SerializeField] private PassPromoPanel promoPrefab;

        /// <summary>The one spawned pitch, shared by every bridge — the popup is a screen, not a per-panel widget.</summary>
        private static PassPromoPanel spawned;
        /// <summary>What we actually subscribed to, so OnDisable detaches from the same object it attached to.</summary>
        private PassPromoPanel hooked;

        private void Awake()
        {
            if (panel == null) panel = GetComponent<PassPanel>();
        }

        private void OnEnable()
        {
            if (panel != null) panel.SubscribeRequested += OnSubscribeRequested;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted += OnPurchaseCompleted;
            // A pitch that already exists is hooked now; one that has to be spawned is hooked when it is.
            if (IsInScene(promo)) Hook(promo);
            else if (spawned != null) Hook(spawned);
        }

        private void OnDisable()
        {
            if (panel != null) panel.SubscribeRequested -= OnSubscribeRequested;
            if (hooked != null) hooked.SubscribeRequested -= Buy;
            hooked = null;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted -= OnPurchaseCompleted;
        }

        /// <summary>
        /// A tap asked for Golden. With a pitch wired it is SHOWN first — a subscription is not something to charge
        /// for on a single tap of a locked card, and the totals are the case for the price. The popup then asks.
        /// </summary>
        private void OnSubscribeRequested()
        {
            var pitch = Resolve();
            if (pitch != null) { Hook(pitch); pitch.Show(); return; }
            Buy();
        }

        /// <summary>
        /// The pitch to show: one already in the scene, the one already spawned, or a fresh one from the prefab.
        ///
        /// Mirrors how the shop screen is resolved, and for the same reason: a prefab ASSET cannot be shown — SetActive
        /// on it does nothing and anything it spawned would be parented into the asset. So a prefab dragged into the
        /// scene-instance field by mistake is treated as a prefab source rather than silently doing nothing.
        ///
        /// Spawned at the scene ROOT and kept across loads, because the popup is its own screen: it can be opened from
        /// the pass, from a card, or from anywhere else, and it must not belong to whichever panel happened to ask first.
        /// </summary>
        private PassPromoPanel Resolve()
        {
            if (IsInScene(promo)) return promo;
            if (spawned != null) return spawned;

            var source = promoPrefab != null ? promoPrefab : promo;
            if (source == null) return null;

            spawned = Instantiate(source);
            spawned.name = source.name;
            DontDestroyOnLoad(spawned.gameObject);
            return spawned;
        }

        private void Hook(PassPromoPanel pitch)
        {
            if (pitch == null || hooked == pitch) return;
            if (hooked != null) hooked.SubscribeRequested -= Buy;
            hooked = pitch;
            hooked.SubscribeRequested -= Buy;   // never twice, however often this is reached
            hooked.SubscribeRequested += Buy;
        }

        /// <summary>True only for a real scene object — false for a prefab asset dragged in from the Project window.</summary>
        private static bool IsInScene(PassPromoPanel pitch) => pitch != null && pitch.gameObject.scene.IsValid();

        private void Buy()
        {
            var iap = IapService.Instance;
            if (iap == null) { Debug.LogWarning($"{name}: no IapService — cannot buy the golden pass.", this); return; }
            iap.OnPurchaseCompleted -= OnPurchaseCompleted;
            iap.OnPurchaseCompleted += OnPurchaseCompleted;
            iap.TryPurchase(StoreCatalog.Instance.GoldenPassProductId);
        }

        private void OnPurchaseCompleted(IapService.PurchaseResult result)
        {
            if (result == null) return;
            bool isPass = (result.redeem != null && result.redeem.Pass != null)
                       || string.Equals(result.productId, StoreCatalog.Instance.GoldenPassProductId, System.StringComparison.OrdinalIgnoreCase);
            if (!isPass) return;
            // Granted → the golden track unlocked on the server; anything else → the screen simply shows the current truth.
            _ = PassState.Instance.RefreshAsync(force: true);
        }
    }
}
