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
        /// <summary>One of a bundle card's extra amounts: which currency, and the label that shows it.</summary>
        [System.Serializable]
        public sealed class AmountSlot
        {
            [Tooltip("Chips · Kash · Gems · Coins · VipPoints · Xp")]
            public string rewardId;
            public TMP_Text text;
            [Tooltip("Optional. {0} is the amount, already thousands-separated — \"XP {0}\" gives \"XP 1,000\", " +
                     "\"{0} VIP\" gives \"1,200 VIP\". Empty = the number on its own.")]
            public string format;
        }

        [Header("Product")]
        [Tooltip("The catalog product id, e.g. chips_01, kash_02, piggy_t1_full, golden_pass.")]
        [SerializeField] private string productId = "";
        [SerializeField] private bool refreshOnEnable = true;

        [Header("UI")]
        [SerializeField] private GameObject root;
        [Tooltip("Every control that BUYS this card. Usually one — the card itself — but a card with a separate price bar, " +
                 "or an icon that should also be tappable, lists them all. Empty = the Button on this object.")]
        [SerializeField] private List<Button> purchaseButtons = new List<Button>();
        [SerializeField] private List<TMP_Text> titleTexts = new List<TMP_Text>();
        [SerializeField] private List<TMP_Text> priceTexts = new List<TMP_Text>();
        [Tooltip("The amount the product pays (e.g. 5,000,000) — from the catalog's currency lines.")]
        [SerializeField] private List<TMP_Text> amountTexts = new List<TMP_Text>();
        [Tooltip("A bundle's OTHER amounts, one label per currency: the id, and the text that shows it. The label is " +
                 "switched off when this product pays none of that currency.")]
        [SerializeField] private List<AmountSlot> extraAmounts = new List<AmountSlot>();
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
        [Tooltip("The card's artwork, filled from the product's image urls — same order as the admin's list, back to front. " +
                 "One Image per url. Leave empty on a card whose art is baked into the prefab.")]
        [SerializeField] private List<Image> images = new List<Image>();
        [SerializeField] private GameObject loadingRoot;
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

        [Header("Blocked look")]
        [Tooltip("Fade the WHOLE card when it cannot be bought. Unity's own disabled tint only touches the button's " +
                 "target graphic, so a blocked card otherwise still looks like a live offer with a live price.")]
        [SerializeField] private bool dimWhenBlocked = true;
        [Tooltip("How faded a blocked card is. Low enough to read as off, high enough to still read at all.")]
        [SerializeField, Range(0.2f, 1f)] private float blockedAlpha = 0.45f;
        [Tooltip("Which currency line to show as the amount: Chips or Kash. Empty = the first currency line.")]
        [SerializeField] private string amountCurrency = "";
        [SerializeField] private string amountFormat = "N0";
        [SerializeField] private string bonusFormat = "+{0}%";

        private string lastResolvedPriceText = string.Empty;
        private string lastActiveId;
        /// <summary>The url each art slot is currently showing, so a repaint does not re-fetch what is already there.</summary>
        private string[] artUrls;
        /// <summary>The sprite each art slot was AUTHORED with — what a card falls back to, so it is never blank.</summary>
        private Sprite[] authoredArt;
        /// <summary>Art downloads still in flight for this card — part of what makes it show its loading state.</summary>
        private int artPending;

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
            if (purchaseButtons.Count == 0)
            {
                var own = GetComponent<Button>();
                if (own != null) purchaseButtons.Add(own);
            }
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
        /// <summary>The last refusal logged, so a repaint every catalog refresh does not repeat it.</summary>
        private string lastBlockedLog;

        private void ApplyCatalogTexts()
        {
            if (!StoreCatalog.Instance.TryGet(productId, out var product) || product == null) return;

            // ALWAYS the catalog title — the one authored in the admin. Never the store console's: that is a second
            // copy of the same string, per platform, which an admin cannot retitle, and in the Editor it is Unity's
            // fake store answering "Fake title for chips_01".
            var title = product.Title;
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

            // The card's OTHER amounts, for a bundle that pays more than one thing. A label whose currency this product
            // does not pay is switched OFF, not blanked: the icon is a sprite inside the same text, so hiding the label
            // takes the icon with it and the layout closes up instead of leaving a gap where a line used to be.
            foreach (var slot in extraAmounts)
            {
                if (slot?.text == null) continue;
                var extra = AmountIn(lines, slot.rewardId);
                bool has = extra > 0m;
                // The number is formatted FIRST, then dropped into the slot's own wording — so "XP {0}" keeps the
                // thousands separators the card already uses instead of printing a raw 1000.
                var shown = extra.ToString(amountFormat, CultureInfo.InvariantCulture);
                slot.text.text = !has ? ""
                    : string.IsNullOrWhiteSpace(slot.format) ? shown
                    : SafeFormat(slot.format, shown);
                if (slot.text.gameObject.activeSelf != has) slot.text.gameObject.SetActive(has);
            }

            ApplyArt(product);

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
                t.text = product.BonusPercent > 0 ? SafeFormat(bonusFormat, product.BonusPercent) : "";
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

            // Say WHY, once, whenever the answer changes. The server sends a plain-English reason for every blocked
            // card ("Already purchased.", "This offer has ended.", "Unlocks at level N.", "The store is closed right
            // now."), and without unavailableRoot / reasonTexts wired on a prefab all of those collapse into the same
            // silent grey card — which is impossible to diagnose from the outside.
            string blocked = !canInteract && iap != null
                ? (isOwned ? "owned" : isProcessing ? null : isUnavailable ? "the store has no such product / it is not purchasable"
                   : reason ?? (!iap.IsReady ? "the store is not ready yet" : null))
                : null;
            if (blocked != lastBlockedLog)
            {
                lastBlockedLog = blocked;
                if (blocked != null) Debug.Log($"[Store] '{productId}' is not buyable: {blocked}", this);
            }

            if (root != null) root.SetActive(!(hideRootWhenOwned && isOwned));
            foreach (var b in purchaseButtons) if (b != null) b.interactable = canInteract;

            // Fade and deaden the WHOLE card, not just its button. A CanvasGroup is what reaches every graphic and
            // every Selectable inside — including ones a card type adds later — where Button.interactable only tints
            // its own target graphic. Added rather than authored, so no card prefab has to remember it.
            //
            // blocksRaycasts stays TRUE on purpose: the tap still has to land so ButtonSound can answer with the
            // denied sound. Silence is the one response a blocked card must not give.
            if (dimWhenBlocked)
            {
                var dimTarget = root != null ? root : gameObject;
                if (!dimTarget.TryGetComponent<CanvasGroup>(out var group))
                    group = dimTarget.AddComponent<CanvasGroup>();
                bool blockedLook = !canInteract && !isProcessing;   // processing has its own loading overlay
                group.alpha = blockedLook ? Mathf.Clamp(blockedAlpha, 0.2f, 1f) : 1f;
                group.interactable = !blockedLook;
                group.blocksRaycasts = true;
            }
            // The card is BUSY for three reasons, and they look the same to a player: a purchase is in flight, its art is
            // still coming down, or the catalog has not resolved this id yet (the store is still loading, or an admin
            // renamed the product). All three mean "nothing here to act on yet".
            bool unresolved = !StoreCatalog.Instance.TryGet(productId, out _);
            if (loadingRoot != null) loadingRoot.SetActive(isProcessing || artPending > 0 || unresolved);
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
            // A card that CANNOT be bought must not advertise a price. This case used to fall through to the store's
            // localised price, so a one-per-user pack the player had already bought went on quoting $0.01 next to a
            // dead button — which reads as a broken card rather than a spent offer.
            else if (isUnavailable || reason != null) priceText = unavailablePriceText;
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

        /// <summary>A mistyped format in the inspector must cost the label, not the whole card: string.Format throws on a
        /// stray brace, and this runs inside the paint that fills title, price and amount too.</summary>
        private static string SafeFormat(string format, decimal value)
            => SafeFormat(format, value.ToString(CultureInfo.InvariantCulture));

        private static string SafeFormat(string format, string value)
        {
            if (string.IsNullOrEmpty(format)) return value;
            try { return string.Format(CultureInfo.InvariantCulture, format, value); }
            catch (FormatException) { return value; }
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

        /// <summary>
        /// Put the product's artwork on the card.
        ///
        /// The art is the CATALOG's, so a pack can be re-skinned for a promotion without a client build. Two things this
        /// has to get right, both because a lane reuses one card across products:
        /// <list type="bullet">
        /// <item>a slot already showing the right url is left alone — a repaint (a scroll, a sale tick, a catalog
        /// refresh) must not re-fetch or flicker;</item>
        /// <item>a slot switching to a DIFFERENT product blanks until the new art lands, and the download's callback
        /// checks the url is still the one wanted. Otherwise a slow fetch finishes onto a card that has since been
        /// rebound, and the player reads the wrong pack's picture over the right pack's price.</item>
        /// </list>
        /// A product with no image url keeps whatever the prefab was authored with.
        /// </summary>
        private void ApplyArt(StoreProductDto product)
        {
            if (images.Count == 0) return;
            if (artUrls == null || artUrls.Length != images.Count) artUrls = new string[images.Count];

            // Remembered ONCE, before anything is overwritten: this is what the card falls back to, and re-reading it
            // later would just capture the last product's downloaded art instead of the artist's.
            if (authoredArt == null || authoredArt.Length != images.Count)
            {
                authoredArt = new Sprite[images.Count];
                for (int i = 0; i < images.Count; i++) authoredArt[i] = images[i] != null ? images[i].sprite : null;
            }

            var urls = product?.Images;
            for (int i = 0; i < images.Count; i++)
            {
                var img = images[i];
                if (img == null) continue;

                string url = urls != null && i < urls.Count ? urls[i] : null;
                if (string.IsNullOrWhiteSpace(url))
                {
                    // Nothing in the catalog for this slot: the authored sprite stands, and stands VISIBLY even if the
                    // slot had been switched off for some other product's download.
                    artUrls[i] = null;
                    ShowAuthored(img, i);
                    continue;
                }
                if (string.Equals(url, artUrls[i], StringComparison.Ordinal)) continue;

                artUrls[i] = url;

                if (PlayCard.Core.RemoteImage.TryGetCached(url, out var cached) && cached != null)
                {
                    img.sprite = cached;
                    img.enabled = true;
                    continue;
                }

                // While the new art downloads the card wears its OWN authored sprite — never the previous product's
                // picture, and never a hole. A dead link therefore leaves a designed card rather than a blank one.
                ShowAuthored(img, i);

                int slot = i;
                string wanted = url;
                artPending++;                       // the card shows its loading state until every slot has answered
                PlayCard.Core.RemoteImage.Load(url, sprite =>
                {
                    artPending = Mathf.Max(0, artPending - 1);
                    if (this == null || images.Count <= slot) return;
                    UpdateVisualState();            // the spinner comes off the moment the last download lands
                    var target = images[slot];
                    if (target == null) return;
                    // Rebound while this was in flight — the answer is for a product this card no longer shows.
                    if (artUrls == null || slot >= artUrls.Length || !string.Equals(artUrls[slot], wanted, StringComparison.Ordinal)) return;
                    if (sprite == null) return;   // nothing arrived; the authored sprite already on screen stays
                    target.sprite = sprite;
                    target.enabled = true;
                });
            }
        }

        /// <summary>Put the prefab's own sprite back on a slot and make sure it is visible.</summary>
        private void ShowAuthored(Image img, int slot)
        {
            if (img == null) return;
            var sprite = authoredArt != null && slot < authoredArt.Length ? authoredArt[slot] : null;
            if (sprite != null) img.sprite = sprite;
            // Only a slot with SOMETHING to show is enabled: a card authored with an empty Image stays empty rather
            // than turning into a white box.
            img.enabled = img.sprite != null;
        }

        /// <summary>
        /// What a product's lines pay for one reward id. Ids are matched case-insensitively — "chips", "Chips" and
        /// "CHIPS" are the same thing here as everywhere else.
        ///
        /// XP is the exception that needs a rule: <c>RewardGrant.Xp(amount)</c> carries a KIND and no id at all, so an
        /// id match alone would silently show nothing for it. A line with no id is matched by its kind instead.
        /// </summary>
        private static decimal AmountIn(IEnumerable<Khela.Common.Rewards.RewardGrant> lines, string rewardId)
        {
            if (lines == null || string.IsNullOrWhiteSpace(rewardId)) return 0m;
            decimal sum = 0m;
            foreach (var l in lines)
            {
                if (l == null) continue;
                bool match = !string.IsNullOrEmpty(l.Id)
                    ? string.Equals(l.Id, rewardId, StringComparison.OrdinalIgnoreCase)
                    : l.Kind == Khela.Common.Rewards.RewardKind.Xp && string.Equals("Xp", rewardId, StringComparison.OrdinalIgnoreCase);
                if (match) sum += l.Amount;
            }
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
            foreach (var b in purchaseButtons)
            {
                if (b == null) continue;
                // Remove first: BindButton runs from Awake, and a card re-bound by a lane must not stack a second
                // listener and fire two purchases from one tap.
                b.onClick.RemoveListener(HandlePurchaseClicked);
                b.onClick.AddListener(HandlePurchaseClicked);
            }
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
