using System.Collections.Generic;
using Khela.Common.Store;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Store
{
    /// <summary>
    /// The shop screen itself — the one thing above the lanes that talks to the server.
    ///
    /// Each <see cref="ShopSection"/> fills itself from whatever the catalog currently holds, and each
    /// <see cref="StorePurchaseButton"/> paints itself from its product. Nobody, though, was responsible for FETCHING
    /// the catalog when the screen opens, for the moment before the first answer arrives, or for the case where the
    /// store is off. That is this.
    ///
    /// Three states, one of them showing at a time: LOADING while the first fetch is in flight (a cached catalog from
    /// disk means it is usually skipped), UNAVAILABLE when the server's kill switch is off or this platform has no
    /// store, and the shop itself otherwise.
    ///
    /// It does not touch balances. A redeem goes through <c>BalanceChangingAsync</c>, so the wallet and every balance
    /// HUD repaint themselves; a shop that also pushed a number would be a second writer racing the first. What it
    /// DOES do after a purchase is re-fetch the catalog, because availability changed: a one-per-user pack is now
    /// owned, and only the server knows that.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShopScreen : MonoBehaviour
    {
        [Header("Roots — one shows at a time")]
        [Tooltip("The shop content: the lanes, the tabs, everything the player buys from.")]
        [SerializeField] private GameObject contentRoot;
        [Tooltip("Shown only while the FIRST catalog fetch is in flight. A cached catalog means it never appears.")]
        [SerializeField] private GameObject loadingRoot;
        [Tooltip("Shown when the store is off server-side, or this platform has no store (an editor build, a kill switch).")]
        [SerializeField] private GameObject unavailableRoot;
        [Tooltip("Optional. Why the shop is unavailable, in the player's terms.")]
        [SerializeField] private List<TMP_Text> unavailableTexts = new List<TMP_Text>();

        [Header("Close")]
        [Tooltip("Everything that closes the shop — the back arrow, an X, a tap-catcher behind the panel. A list because " +
                 "a screen this size usually grows more than one way out, and each would otherwise need its own wiring.")]
        [SerializeField] private List<Button> backButtons = new List<Button>();

        [Header("Text")]
        [SerializeField] private string storeOffText = "The shop is closed right now. Please try again later.";
        [SerializeField] private string platformOffText = "Purchases aren't available on this device.";
        [SerializeField] private string offlineText = "Couldn't reach the shop. Check your connection and try again.";

        [Header("Behaviour")]
        [Tooltip("Re-fetch every time the screen opens rather than trusting the 60 s freshness window. On, because a sale " +
                 "that started or ended while the player was at a table should be on the card they are looking at.")]
        [SerializeField] private bool forceRefreshOnOpen = true;
        [Tooltip("After a purchase is GRANTED, re-fetch so per-user availability is right — a one-per-user pack that is now " +
                 "owned, a limit that is now reached.")]
        [SerializeField] private bool refreshAfterPurchase = true;

        private bool fetching;
        private bool everAnswered;

        private void Awake()
        {
            foreach (var b in backButtons) if (b != null) b.onClick.AddListener(Close);
        }

        private void OnDestroy()
        {
            foreach (var b in backButtons) if (b != null) b.onClick.RemoveListener(Close);
        }

        /// <summary>Close the shop. The panel is kept alive, not destroyed, so reopening is instant.</summary>
        public void Close()
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        private void OnEnable()
        {
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed += HandleCatalogChanged;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted += HandlePurchaseCompleted;
            Open();
        }

        private void OnDisable()
        {
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed -= HandleCatalogChanged;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted -= HandlePurchaseCompleted;
        }

        /// <summary>Open (or re-open) the shop: show what we already have, then go and check.</summary>
        public void Open()
        {
            if (!gameObject.activeSelf)
            {
                // OnEnable calls straight back here, so this returns and lets it do the work once rather than twice.
                gameObject.SetActive(true);
                return;
            }

            var catalog = StoreCatalog.Instance;
            if (catalog != null)
            {
                // The disk cache is what makes the shop feel instant: last session's product list is on screen before the
                // API answers. Prices still come from the store, availability still from the fetch below.
                catalog.LoadCached();
                if (catalog.Loaded) everAnswered = true;
            }
            // The fetch is kicked FIRST and the state painted after: FetchAsync marks itself in flight synchronously,
            // before its first await, so this paints LOADING. Painting first would read "not loaded, not fetching" and
            // flash "couldn't reach the shop" for a frame every single time the shop opens.
            _ = FetchAsync();
            ApplyState();
        }

        /// <summary>Fetch the catalog, coalescing with anything already in flight.</summary>
        public async System.Threading.Tasks.Task FetchAsync()
        {
            var catalog = StoreCatalog.Instance;
            if (catalog == null || fetching) return;
            fetching = true;
            try
            {
                await catalog.RefreshAsync(force: forceRefreshOnOpen);
                everAnswered = everAnswered || catalog.Loaded;
            }
            finally
            {
                fetching = false;
                ApplyState();
            }
        }

        private void HandleCatalogChanged(StoreCatalogDto _)
        {
            everAnswered = true;
            ApplyState();
        }

        private void HandlePurchaseCompleted(IapService.PurchaseResult result)
        {
            if (!refreshAfterPurchase || result == null) return;
            // Only a GRANTED purchase changes what the shop may sell. A cancel or a failure leaves the catalog exactly as
            // it was, and re-fetching on those would put a spinner in front of a player who just backed out of a sheet.
            if (result.status != IapService.PurchaseStatus.Success) return;
            _ = FetchAsync();
        }

        private void ApplyState()
        {
            var catalog = StoreCatalog.Instance;
            bool loaded = catalog != null && catalog.Loaded;
            bool loading = !loaded && fetching;
            bool usable = loaded && catalog.Enabled && catalog.PlatformEnabled;

            if (loadingRoot != null) loadingRoot.SetActive(loading);
            if (contentRoot != null) contentRoot.SetActive(usable);
            if (unavailableRoot != null) unavailableRoot.SetActive(!usable && !loading);

            if (!usable && !loading && unavailableTexts.Count > 0)
            {
                // Three different failures, three different things to tell the player: we never got an answer, the shop is
                // shut, or this build simply has no store behind it.
                string reason = !everAnswered ? offlineText
                    : catalog != null && !catalog.Enabled ? storeOffText
                    : platformOffText;
                foreach (var t in unavailableTexts) if (t != null) t.text = reason;
            }
        }
    }
}
