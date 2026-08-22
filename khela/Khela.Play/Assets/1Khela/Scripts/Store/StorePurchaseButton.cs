using System;
using System.Collections.Generic;
using System.Globalization;
using Khela.Common.Store;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Store
{
    /// <summary>
    /// A buy button for ONE store product — the WGWB <c>ShopIapPurchaseButton</c> ported to the Khela store. Type the
    /// product id (the catalog id: <c>chips_01</c>, <c>kash_01</c>, <c>golden_pass</c>…), drop it on a card, wire the
    /// texts. The PRICE is the store's localized string (never ours); title / amount / bonus come from the server catalog;
    /// the click goes to <see cref="IapService.TryPurchase"/>. It disables itself while the store isn't ready, the product
    /// is processing, the store says it's unavailable, or the server says this player can't buy it right now (and shows
    /// the server's reason in <c>reasonTexts</c>). Put <c>ButtonSound</c> / <c>ButtonJuice</c> beside it as usual.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StorePurchaseButton : MonoBehaviour
    {
        [Header("Product")]
        [Tooltip("The catalog product id, e.g. chips_01, kash_02, piggy_t1_full, golden_pass.")]
        [SerializeField] private string productId = "";
        [SerializeField] private bool refreshOnEnable = true;

        [Header("UI")]
        [SerializeField] private GameObject root;
        [SerializeField] private Button purchaseButton;
        [SerializeField] private List<TMP_Text> titleTexts = new List<TMP_Text>();
        [SerializeField] private List<TMP_Text> priceTexts = new List<TMP_Text>();
        [Tooltip("The amount the product pays (e.g. 5,000,000) — from the catalog's currency lines.")]
        [SerializeField] private List<TMP_Text> amountTexts = new List<TMP_Text>();
        [Tooltip("Bonus % badge text (e.g. +33%). Hidden when 0.")]
        [SerializeField] private List<TMP_Text> bonusTexts = new List<TMP_Text>();
        [Tooltip("Badge text (e.g. BEST VALUE). Hidden when empty.")]
        [SerializeField] private List<TMP_Text> badgeTexts = new List<TMP_Text>();
        [Tooltip("Shown with the server's reason when the player can't buy this right now (limit reached, level gate…).")]
        [SerializeField] private List<TMP_Text> reasonTexts = new List<TMP_Text>();
        [SerializeField] private GameObject loadingRoot;
        [SerializeField] private GameObject buttonTextRoot;
        [SerializeField] private GameObject ownedRoot;
        [SerializeField] private GameObject unavailableRoot;
        [SerializeField] private bool hideRootWhenOwned;

        [Header("Text")]
        [SerializeField] private string loadingPriceText = "...";
        [SerializeField] private string unavailablePriceText = "--";
        [SerializeField] private string ownedPriceText = "OWNED";
        [Tooltip("Which currency line to show as the amount: Chips or Kash. Empty = the first currency line.")]
        [SerializeField] private string amountCurrency = "";
        [SerializeField] private string amountFormat = "N0";
        [SerializeField] private string bonusFormat = "+{0}%";

        private string lastResolvedPriceText = string.Empty;

        public string ProductId => productId;

        /// <summary>Retarget the button at runtime (a card reused across products).</summary>
        public void SetProduct(string id)
        {
            productId = id ?? "";
            Refresh();
        }

        private void Awake()
        {
            if (root == null) root = gameObject;
            if (purchaseButton == null) purchaseButton = GetComponent<Button>();
            CacheInitialDisplayedPrice();
            BindButton();
            Refresh();
        }

        private void OnEnable()
        {
            Subscribe();
            if (refreshOnEnable) Refresh();
        }

        private void OnDisable() => Unsubscribe();

        public void Refresh()
        {
            ApplyCatalogTexts();
            UpdateVisualState();
        }

        private void HandlePurchaseClicked()
        {
            var iap = IapService.Instance;
            if (iap == null) { Debug.LogWarning($"{name}: no IapService in the scene."); return; }
            iap.TryPurchase(productId);
        }

        private void ApplyCatalogTexts()
        {
            if (!StoreCatalog.Instance.TryGet(productId, out var product) || product == null) return;

            var title = IapService.Instance != null ? IapService.Instance.GetLocalizedTitle(productId, product.Title) : product.Title;
            foreach (var t in titleTexts) if (t != null) t.text = title ?? "";

            decimal amount = 0m;
            if (!string.IsNullOrWhiteSpace(amountCurrency)) amount = StoreCatalog.AmountOf(product, amountCurrency);
            else if (product.Lines != null)
                foreach (var line in product.Lines)
                    if (line != null && line.Kind == (int)Khela.Common.Rewards.RewardKind.Currency) { amount = StoreCatalog.AmountOf(product, line.Id); break; }
            foreach (var t in amountTexts) if (t != null) t.text = amount > 0m ? amount.ToString(amountFormat, CultureInfo.InvariantCulture) : "";

            foreach (var t in bonusTexts)
            {
                if (t == null) continue;
                t.text = product.BonusPercent > 0 ? string.Format(CultureInfo.InvariantCulture, bonusFormat, product.BonusPercent) : "";
                t.gameObject.SetActive(product.BonusPercent > 0);
            }
            foreach (var t in badgeTexts)
            {
                if (t == null) continue;
                t.text = product.Badge ?? "";
                t.gameObject.SetActive(!string.IsNullOrWhiteSpace(product.Badge));
            }
        }

        private void UpdateVisualState()
        {
            var iap = IapService.Instance;
            bool isOwned = iap != null && iap.IsOwned(productId);
            bool isProcessing = iap != null && iap.IsProcessing(productId);
            bool isUnavailable = iap != null && iap.IsProductExplicitlyUnavailable(productId);
            string reason = iap != null ? iap.IneligibilityReason(productId) : null;
            bool canInteract = iap != null && iap.IsReady && !isOwned && !isProcessing && !isUnavailable && reason == null;

            if (root != null) root.SetActive(!(hideRootWhenOwned && isOwned));
            if (purchaseButton != null) purchaseButton.interactable = canInteract;
            if (loadingRoot != null) loadingRoot.SetActive(isProcessing);
            if (buttonTextRoot != null) buttonTextRoot.SetActive(!isProcessing);
            if (ownedRoot != null) ownedRoot.SetActive(isOwned);
            if (unavailableRoot != null) unavailableRoot.SetActive(!isOwned && (isUnavailable || reason != null));
            foreach (var t in reasonTexts)
            {
                if (t == null) continue;
                t.text = reason ?? "";
                t.gameObject.SetActive(reason != null);
            }

            string priceText = lastResolvedPriceText;
            if (isProcessing) priceText = loadingPriceText;
            else if (isOwned) priceText = ownedPriceText;
            else if (iap != null)
            {
                var resolved = iap.GetLocalizedPriceString(productId, string.Empty);
                if (!string.IsNullOrWhiteSpace(resolved)) { lastResolvedPriceText = resolved; priceText = resolved; }
                else if (string.IsNullOrWhiteSpace(priceText) && StoreCatalog.Instance.TryGet(productId, out var p) && p.UsdReference > 0m)
                    priceText = "$" + p.UsdReference.ToString("0.00", CultureInfo.InvariantCulture);   // reference fallback until the store answers
            }
            if (string.IsNullOrWhiteSpace(priceText)) priceText = unavailablePriceText;
            foreach (var t in priceTexts) if (t != null) t.text = priceText;
        }

        private void CacheInitialDisplayedPrice()
        {
            foreach (var text in priceTexts)
            {
                if (text == null) continue;
                var initial = text.text != null ? text.text.Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(initial) || string.Equals(initial, unavailablePriceText)) continue;
                lastResolvedPriceText = initial;
                break;
            }
        }

        private void BindButton()
        {
            if (purchaseButton == null) return;
            purchaseButton.onClick.RemoveListener(HandlePurchaseClicked);
            purchaseButton.onClick.AddListener(HandlePurchaseClicked);
        }

        private void Subscribe()
        {
            var iap = IapService.Instance;
            if (iap != null)
            {
                iap.OnCatalogUpdated -= HandleCatalogUpdated; iap.OnCatalogUpdated += HandleCatalogUpdated;
                iap.OnInitializationStateChanged -= HandleInitializationStateChanged; iap.OnInitializationStateChanged += HandleInitializationStateChanged;
                iap.OnProcessingStateChanged -= HandleProcessingStateChanged; iap.OnProcessingStateChanged += HandleProcessingStateChanged;
                iap.OnPurchaseCompleted -= HandlePurchaseCompleted; iap.OnPurchaseCompleted += HandlePurchaseCompleted;
            }
            StoreCatalog.Instance.Changed -= HandleStoreCatalogChanged;
            StoreCatalog.Instance.Changed += HandleStoreCatalogChanged;
        }

        private void Unsubscribe()
        {
            var iap = IapService.Instance;
            if (iap != null)
            {
                iap.OnCatalogUpdated -= HandleCatalogUpdated;
                iap.OnInitializationStateChanged -= HandleInitializationStateChanged;
                iap.OnProcessingStateChanged -= HandleProcessingStateChanged;
                iap.OnPurchaseCompleted -= HandlePurchaseCompleted;
            }
            StoreCatalog.Instance.Changed -= HandleStoreCatalogChanged;
        }

        private void HandleCatalogUpdated() => Refresh();
        private void HandleStoreCatalogChanged(StoreCatalogDto _) => Refresh();
        private void HandleInitializationStateChanged(IapService.InitializationState _, string __) => UpdateVisualState();
        private void HandleProcessingStateChanged(string id, bool _) { if (string.Equals(id, productId, StringComparison.Ordinal)) UpdateVisualState(); }
        private void HandlePurchaseCompleted(IapService.PurchaseResult result)
        {
            if (result == null || !string.Equals(result.productId, productId, StringComparison.Ordinal)) return;
            // Limits / ownership may have changed with this purchase: refresh the catalog's per-user flags, then repaint.
            _ = StoreCatalog.Instance.RefreshAsync(force: true);
            UpdateVisualState();
        }
    }
}
