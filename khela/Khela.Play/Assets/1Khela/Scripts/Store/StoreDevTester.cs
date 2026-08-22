using System.Linq;
using UnityEngine;

namespace PlayCard.Store
{
    /// <summary>
    /// DEV ONLY. Drives a purchase from the inspector so the whole store rail can be exercised without any shop UI:
    /// Unity's fake store → <c>POST /api/store/redeem</c> → server verification → wallet credit → HUD.
    ///
    /// Drop it on any GameObject in a running scene (after sign-in) and use the component's context menu (the ⋮ next to
    /// the component header, or right-click it): <b>Buy product</b>. The result is logged, including the server's status,
    /// so a failure tells you which half broke.
    ///
    /// Deliberately not wired to any button — it exists so the data layer can be tested before the Shop screen exists,
    /// and it should never ship inside a UI prefab. It compiles out of non-development builds entirely.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StoreDevTester : MonoBehaviour
    {
        [Tooltip("A catalog product id: chips_01 · chips_02 · kash_01 · piggy_t1_full · golden_pass · vip_booster_time …")]
        [SerializeField] private string productId = "chips_01";

        [ContextMenu("Store ▸ Log state")]
        public void LogState()
        {
            var iap = IapService.Instance;
            if (iap == null) { Debug.LogWarning("[StoreDevTester] no IapService yet — is the app signed in?"); return; }
            var catalog = StoreCatalog.Instance;
            Debug.Log($"[StoreDevTester] platform={iap.Platform} state={iap.State} ({iap.LastStatusMessage}) " +
                      $"catalogLoaded={catalog.Loaded} storeOpen={catalog.Enabled} platformOn={catalog.PlatformEnabled} " +
                      $"products={catalog.Products.Count} fetchedFromStore={(iap.HasFetchedProduct(productId) ? "yes" : "no")} " +
                      $"price={iap.GetLocalizedPriceString(productId, "—")}");
            if (catalog.Products.Count > 0)
                Debug.Log("[StoreDevTester] catalog: " + string.Join(", ", catalog.Products.Take(12).Select(p => p.Id + (p.Purchasable ? "" : $"({p.Reason})"))));
        }

        [ContextMenu("Store ▸ Refresh catalog")]
        public async void RefreshCatalog()
        {
            await StoreCatalog.Instance.RefreshAsync(force: true);
            LogState();
        }

        /// <summary>Re-run Unity IAP start-up — use after a <c>Failed</c> state (e.g. the catalog wasn't up when the app booted).</summary>
        [ContextMenu("Store ▸ Initialize (retry)")]
        public void InitializeStore()
        {
            var iap = IapService.Instance;
            if (iap == null) { Debug.LogWarning("[StoreDevTester] no IapService yet — is the app signed in?"); return; }
            Debug.Log($"[StoreDevTester] re-initializing from state {iap.State}…");
            iap.Initialize();
        }

        [ContextMenu("Store ▸ Buy product")]
        public void Buy()
        {
            var iap = IapService.Instance;
            if (iap == null) { Debug.LogWarning("[StoreDevTester] no IapService yet — is the app signed in?"); return; }

            iap.OnPurchaseCompleted -= Report;
            iap.OnPurchaseCompleted += Report;
            Debug.Log($"[StoreDevTester] buying '{productId}' on {iap.Platform}…");
            iap.TryPurchase(productId);
        }

        [ContextMenu("Store ▸ Restore purchases")]
        public void Restore() => IapService.Instance?.RestoreTransactions();

        private void Report(IapService.PurchaseResult r)
        {
            if (r == null) return;
            var redeem = r.redeem;
            var grants = redeem?.Grants == null ? "—" : string.Join(", ", redeem.Grants.Select(g => $"{g.Amount:N0} {g.Id}"));
            var line = $"[StoreDevTester] {r.productId}: {r.status} — {r.message}";
            if (redeem != null) line += $" | serverStatus={redeem.Status} chips={redeem.NewChipBalance:N0} kash={redeem.NewKashBalance:N0} granted=[{grants}] test={redeem.IsTest}";
            if (r.status == IapService.PurchaseStatus.Success) Debug.Log(line);
            else Debug.LogWarning(line);
        }

        private void OnDestroy()
        {
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted -= Report;
        }
    }
}
