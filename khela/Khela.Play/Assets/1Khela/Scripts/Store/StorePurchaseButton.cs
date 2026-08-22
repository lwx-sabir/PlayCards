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
        [Tooltip("RIBBON badge text (e.g. 2X VALUE) — the corner banner. Hidden when empty.")]
        [SerializeField] private List<TMP_Text> badgeTexts = new List<TMP_Text>();
        [Tooltip("Root of the ribbon (the banner graphic). Hidden when the ribbon text is empty. Optional.")]
        [SerializeField] private GameObject badgeRoot;
        [Tooltip("CORNER badge text (e.g. POPULAR) — the hex mark, independent of the ribbon. Hidden when empty.")]
        [SerializeField] private List<TMP_Text> badge2Texts = new List<TMP_Text>();
        [Tooltip("Root of the corner badge (the hex graphic). Hidden when the corner text is empty. Optional.")]
        [SerializeField] private GameObject badge2Root;
        [Tooltip("Shown with the server's reason when the player can't buy this right now (limit reached, level gate…).")]
        [SerializeField] private List<TMP_Text> reasonTexts = new List<TMP_Text>();
        [SerializeField] private GameObject loadingRoot;
        [SerializeField] private GameObject buttonTextRoot;
        [SerializeField] private GameObject ownedRoot;
        [SerializeField] private GameObject unavailableRoot;
        [SerializeField] private bool hideRootWhenOwned;

        [Header("Sale (optional)")]
        [Tooltip("Active while a sale runs on this product (the ribbon). Leave empty if the card has no sale treatment.")]
        [SerializeField] private GameObject saleRoot;
        [Tooltip("The ribbon text: the sale's label, or +X% / -X% when it has none.")]
        [SerializeField] private List<TMP_Text> saleTexts = new List<TMP_Text>();
        [Tooltip("PRICE-OFF only: the REGULAR store price, shown struck through beside the live sale price. Hidden otherwise.")]
        [SerializeField] private List<TMP_Text> strikePriceTexts = new List<TMP_Text>();
        [Tooltip("Time left in the sale, ticking once a second against the SERVER clock. Hidden when no sale.")]
        [SerializeField] private List<TMP_Text> saleCountdownTexts = new List<TMP_Text>();
        [SerializeField] private string saleBonusFormat = "+{0}%";
        [SerializeField] private string saleOffFormat = "-{0}%";

        [Header("Text")]
        [SerializeField] private string loadingPriceText = "...";
        [SerializeField] private string unavailablePriceText = "--";
        [SerializeField] private string ownedPriceText = "OWNED";
        [Tooltip("Which currency line to show as the amount: Chips or Kash. Empty = the first currency line.")]
        [SerializeField] private string amountCurrency = "";
        [SerializeField] private string amountFormat = "N0";
        [SerializeField] private string bonusFormat = "+{0}%";

        private string lastResolvedPriceText = string.Empty;
        private string lastActiveId;

        public string ProductId => productId;

        /// <summary>
        /// The SKU a tap actually buys: the product's own id, or — while a PRICE-OFF sale runs on it — the cheaper sale SKU
        /// the server named. The card stays the regular product's card; only what it sells changes.
        /// </summary>
        public string ActiveProductId
        {
            get
            {
                var sale = PriceOffSale();
                return sale != null ? sale.SaleProductId : productId;
            }
        }

        /// <summary>
        /// The PRICE-OFF sale this card can actually honour right now: the server names a sale SKU AND the store has priced it.
        /// Unity IAP fetches product definitions once at start-up, so a SKU added to the catalog mid-session (or not yet live
        /// in the console) has no store product; switching the card to it would leave a dead button with a stale price.
        /// In that case the card keeps selling the regular product at its regular price, with no ribbon and no struck price —
        /// the next app start picks the sale up. A value-bonus sale is unaffected (same SKU).
        /// </summary>
        private StoreSaleDto PriceOffSale()
        {
            if (!StoreCatalog.Instance.TryGet(productId, out var p) || p?.Sale == null) return null;
            if (p.Sale.Kind != StoreSaleKind.PriceOff || string.IsNullOrWhiteSpace(p.Sale.SaleProductId)) return null;
            var iap = IapService.Instance;
            if (iap == null || !iap.HasFetchedProduct(p.Sale.SaleProductId) || iap.IsProductExplicitlyUnavailable(p.Sale.SaleProductId)) return null;
            return p.Sale;
        }

        /// <summary>The sale to SHOW on this card — a value bonus as-is, a price-off only when <see cref="PriceOffSale"/> can honour it.</summary>
        private StoreSaleDto ShownSale(StoreProductDto product)
        {
            if (product?.Sale == null || product.Sale.Kind == StoreSaleKind.None) return null;
            return product.Sale.Kind == StoreSaleKind.PriceOff ? PriceOffSale() : product.Sale;
        }

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
            // Remember the exact SKU sent: if the sale ends while the sheet is open, ActiveProductId flips back to the regular
            // product, and the card must still show "processing" for — and react to the result of — the SKU actually bought.
            var id = ActiveProductId;
            buyingId = iap.TryPurchase(id) ? id : null;
            UpdateVisualState();
        }

        /// <summary>The SKU of the purchase this card started and has not yet seen complete; null otherwise.</summary>
        private string buyingId;

        private void ApplyCatalogTexts()
        {
            if (!StoreCatalog.Instance.TryGet(productId, out var product) || product == null) return;

            var title = IapService.Instance != null ? IapService.Instance.GetLocalizedTitle(productId, product.Title) : product.Title;
            foreach (var t in titleTexts) if (t != null) t.text = title ?? "";

            // The amount shown is what the SERVER will grant: during a value-bonus sale that is the sale's boosted lines
            // (the same formula the grant uses), otherwise the product's own.
            var sale = ShownSale(product);
            var lines = sale != null && sale.Kind == StoreSaleKind.ValueBonus && sale.Lines != null ? sale.Lines : product.Lines;
            decimal amount = 0m;
            if (!string.IsNullOrWhiteSpace(amountCurrency)) amount = AmountIn(lines, amountCurrency);
            else if (lines != null)
                foreach (var line in lines)
                    if (line != null && line.Kind == Khela.Common.Rewards.RewardKind.Currency) { amount = AmountIn(lines, line.Id); break; }
            foreach (var t in amountTexts) if (t != null) t.text = amount > 0m ? amount.ToString(amountFormat, CultureInfo.InvariantCulture) : "";

            // Sale ribbon: the label, or ±X%.
            string saleText = null;
            if (sale != null)
                saleText = !string.IsNullOrWhiteSpace(sale.Label) ? sale.Label
                         : string.Format(CultureInfo.InvariantCulture, sale.Kind == StoreSaleKind.PriceOff ? saleOffFormat : saleBonusFormat, sale.Percent);
            if (saleRoot != null) saleRoot.SetActive(saleText != null);
            foreach (var t in saleTexts)
            {
                if (t == null) continue;
                t.text = saleText ?? "";
                t.gameObject.SetActive(saleText != null);
            }
            TickSale();

            foreach (var t in bonusTexts)
            {
                if (t == null) continue;
                t.text = product.BonusPercent > 0 ? string.Format(CultureInfo.InvariantCulture, bonusFormat, product.BonusPercent) : "";
                t.gameObject.SetActive(product.BonusPercent > 0);
            }
            // Two independent marks: the ribbon says something about the PRICE ("2X VALUE"), the corner hex about other
            // players ("POPULAR"). A card commonly wears both, so they are separate catalog fields, not one string.
            SetBadge(badgeTexts, badgeRoot, product.Badge);
            SetBadge(badge2Texts, badge2Root, product.Badge2);
        }

        private void UpdateVisualState()
        {
            var iap = IapService.Instance;
            // Everything about BUYING is asked of the SKU the tap will buy (the sale SKU during a price-off sale); ownership
            // is the regular product's (a non-consumable / subscription is owned whichever SKU bought it).
            var activeId = ActiveProductId;
            bool onPriceOff = !string.Equals(activeId, productId, StringComparison.Ordinal);
            if (!string.Equals(activeId, lastActiveId, StringComparison.Ordinal))
            {
                // The SKU this card sells changed (a price-off sale began or ended): a price cached from the other SKU must
                // never be shown as this one's.
                lastActiveId = activeId;
                lastResolvedPriceText = string.Empty;
            }
            bool isOwned = iap != null && iap.IsOwned(productId);
            bool isProcessing = iap != null && (iap.IsProcessing(activeId) || (onPriceOff && iap.IsProcessing(productId))
                                                || (buyingId != null && iap.IsProcessing(buyingId)));
            bool isUnavailable = iap != null && iap.IsProductExplicitlyUnavailable(activeId);
            string reason = iap != null ? iap.IneligibilityReason(activeId) : null;
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
                var resolved = iap.GetLocalizedPriceString(activeId, string.Empty);
                if (!string.IsNullOrWhiteSpace(resolved)) { lastResolvedPriceText = resolved; priceText = resolved; }
                else if (string.IsNullOrWhiteSpace(priceText) && StoreCatalog.Instance.TryGet(activeId, out var p) && p.UsdReference > 0m)
                    priceText = "$" + p.UsdReference.ToString("0.00", CultureInfo.InvariantCulture);   // reference fallback until the store answers
            }
            if (string.IsNullOrWhiteSpace(priceText)) priceText = unavailablePriceText;
            foreach (var t in priceTexts) if (t != null) t.text = priceText;

            // PRICE-OFF: the regular SKU's own store price, struck through beside the live one. Both are real store prices
            // in the buyer's currency — never a reference number, never a made-up "was".
            string strike = null;
            if (onPriceOff && !isOwned && iap != null)
            {
                strike = iap.GetLocalizedPriceString(productId, string.Empty);
                if (string.IsNullOrWhiteSpace(strike)) strike = null;
            }
            foreach (var t in strikePriceTexts)
            {
                if (t == null) continue;
                t.text = strike ?? "";
                t.gameObject.SetActive(strike != null);
            }
        }

        // ---- sale countdown ----

        private float nextSaleTick;
        private float nextExpiredRefreshAt;

        private void Update()
        {
            if (saleCountdownTexts.Count == 0 && saleRoot == null) return;
            if (Time.unscaledTime < nextSaleTick) return;
            nextSaleTick = Time.unscaledTime + 1f;
            TickSale();
        }

        /// <summary>Repaint the countdown from the SERVER clock; when the sale has run out, ask for a fresh catalog so the ribbon drops.</summary>
        private void TickSale()
        {
            var sale = StoreCatalog.Instance.TryGet(productId, out var p) ? ShownSale(p) : null;
            if (sale == null)
            {
                foreach (var t in saleCountdownTexts) if (t != null) t.gameObject.SetActive(false);
                return;
            }
            var left = sale.EndsAtUtc - StoreCatalog.Instance.ServerNowUtc;
            if (left <= TimeSpan.Zero)
            {
                left = TimeSpan.Zero;
                // The server's catalog cache is ~15 s; don't hammer it — one forced refresh, then again every 20 s until it drops.
                if (Time.unscaledTime >= nextExpiredRefreshAt)
                {
                    nextExpiredRefreshAt = Time.unscaledTime + 20f;
                    _ = StoreCatalog.Instance.RefreshAsync(force: true);
                }
            }
            string text = left.TotalHours >= 24 ? $"{(int)left.TotalDays}d {left.Hours}h"
                        : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes:00}m"
                        : $"{left.Minutes:00}:{left.Seconds:00}";
            foreach (var t in saleCountdownTexts)
            {
                if (t == null) continue;
                t.text = text;
                t.gameObject.SetActive(true);
            }
        }

        private static void SetBadge(List<TMP_Text> texts, GameObject root, string value)
        {
            bool on = !string.IsNullOrWhiteSpace(value);
            if (root != null && root.activeSelf != on) root.SetActive(on);
            foreach (var t in texts)
            {
                if (t == null) continue;
                t.text = value ?? "";
                t.gameObject.SetActive(on);
            }
        }

        private static decimal AmountIn(IEnumerable<Khela.Common.Rewards.RewardGrant> lines, string currency)
        {
            if (lines == null) return 0m;
            decimal sum = 0m;
            foreach (var l in lines)
                if (l != null && l.Kind == Khela.Common.Rewards.RewardKind.Currency && string.Equals(l.Id, currency, StringComparison.OrdinalIgnoreCase)) sum += l.Amount;
            return sum;
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
        /// <summary>Is this id one of ours — the product, or the sale SKU it currently sells?</summary>
        private bool IsMine(string id)
            => string.Equals(id, productId, StringComparison.Ordinal)
            || string.Equals(id, ActiveProductId, StringComparison.Ordinal)
            || (buyingId != null && string.Equals(id, buyingId, StringComparison.Ordinal));

        private void HandleProcessingStateChanged(string id, bool _) { if (IsMine(id)) UpdateVisualState(); }
        private void HandlePurchaseCompleted(IapService.PurchaseResult result)
        {
            if (result == null || !IsMine(result.productId)) return;
            if (buyingId != null && string.Equals(result.productId, buyingId, StringComparison.Ordinal)) buyingId = null;
            // Limits / ownership may have changed with this purchase: refresh the catalog's per-user flags, then repaint.
            _ = StoreCatalog.Instance.RefreshAsync(force: true);
            UpdateVisualState();
        }
    }
}
