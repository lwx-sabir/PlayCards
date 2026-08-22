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

        private void Awake()
        {
            if (panel == null) panel = GetComponent<PassPanel>();
        }

        private void OnEnable()
        {
            if (panel != null) panel.SubscribeRequested += OnSubscribeRequested;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted += OnPurchaseCompleted;
        }

        private void OnDisable()
        {
            if (panel != null) panel.SubscribeRequested -= OnSubscribeRequested;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted -= OnPurchaseCompleted;
        }

        private void OnSubscribeRequested()
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
