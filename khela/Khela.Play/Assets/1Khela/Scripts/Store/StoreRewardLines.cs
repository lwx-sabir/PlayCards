using System;
using System.Collections.Generic;
using System.Globalization;
using Khela.Common.Rewards;
using Khela.Common.Store;
using PlayCard.UI;
using TMPro;
using UnityEngine;

namespace PlayCard.Store
{
    /// <summary>
    /// The reward ROWS of a store card — one per line the product pays ("2,000,000 🪙 / Kash 50 / VIP 500"), for bundles
    /// that hand over more than one thing. <see cref="StorePurchaseButton"/> shows a single headline amount, which is right
    /// for a chip or Kash pack; a Starter Pack needs a row each, and how many rows is the CATALOG's decision, not the
    /// prefab's — an admin who adds Gems to the bundle must not need a client build.
    ///
    /// Put it on the card's rows container (a VerticalLayoutGroup), point it at the same product id as the button, and give
    /// it a row prefab with a <see cref="BundleRewardView"/> (icon + amount — the same row the daily bundle uses) plus the
    /// currency icons. It repaints itself on every catalog change, and shows the SALE's boosted amounts while a value bonus
    /// runs — the same numbers the server will grant.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StoreRewardLines : MonoBehaviour
    {
        /// <summary>An icon for one reward id: a currency name ("Chips", "Kash", "Gems", "Coins"), "Xp", or a chest/item id.</summary>
        [Serializable]
        public sealed class IconEntry
        {
            [Tooltip("Chips · Kash · Gems · Coins · Xp — or a chest/item id.")]
            public string rewardId;
            public Sprite icon;
        }

        [Header("Product")]
        [Tooltip("The catalog product id — the same one on this card's StorePurchaseButton.")]
        [SerializeField] private string productId = "";

        [Header("Rows")]
        [Tooltip("Prefab for ONE row: a BundleRewardView (icon + amount). Instantiated under this object.")]
        [SerializeField] private BundleRewardView rowPrefab;
        [Tooltip("Where rows go. Empty = this object's transform (give it a VerticalLayoutGroup).")]
        [SerializeField] private RectTransform rowsParent;
        [Tooltip("Rows beyond this are dropped; 0 = no limit. A card has room for a few, not for a chest's worth.")]
        [SerializeField] private int maxRows = 4;
        [Tooltip("Hide XP lines (they are a side effect of buying, rarely the sell).")]
        [SerializeField] private bool hideXp = true;

        [Header("Icons")]
        [SerializeField] private List<IconEntry> icons = new List<IconEntry>();

        [Header("Optional labels")]
        [Tooltip("A TMP_Text per row prefab is inside BundleRewardView; these are for a SINGLE summary line instead, e.g. \"2,000,000 + 50 Kash\". Optional.")]
        [SerializeField] private List<TMP_Text> summaryTexts = new List<TMP_Text>();
        [SerializeField] private string amountFormat = "#,0";
        [SerializeField] private string summarySeparator = " + ";

        private readonly List<BundleRewardView> spawned = new List<BundleRewardView>();

        public string ProductId => productId;

        /// <summary>Retarget at runtime (a card reused across products).</summary>
        public void SetProduct(string id)
        {
            productId = id ?? "";
            Refresh();
        }

        private void Awake()
        {
            if (rowsParent == null) rowsParent = transform as RectTransform;
        }

        private void OnEnable()
        {
            StoreCatalog.Instance.Changed -= HandleCatalogChanged;
            StoreCatalog.Instance.Changed += HandleCatalogChanged;
            Refresh();
        }

        private void OnDisable() => StoreCatalog.Instance.Changed -= HandleCatalogChanged;

        private void HandleCatalogChanged(StoreCatalogDto _) => Refresh();

        public void Refresh()
        {
            if (!StoreCatalog.Instance.TryGet(productId, out var product) || product == null) { Clear(); return; }

            // During a VALUE-BONUS sale the server sends the boosted lines — show those, so the card promises exactly what
            // the grant will pay. A price-off sale pays the product's own lines (only the price changes).
            var lines = product.Sale != null && product.Sale.Kind == StoreSaleKind.ValueBonus && product.Sale.Lines != null
                ? product.Sale.Lines
                : product.Lines;

            var shown = new List<RewardGrant>();
            foreach (var line in lines ?? new List<RewardGrant>())
            {
                if (line == null || line.Amount <= 0m) continue;
                if (hideXp && line.Kind == RewardKind.Xp) continue;
                shown.Add(line);
                if (maxRows > 0 && shown.Count >= maxRows) break;
            }

            Build(shown);
            Summarise(shown);
        }

        private void Build(List<RewardGrant> lines)
        {
            if (rowPrefab == null || rowsParent == null) return;

            // Grow the pool, never destroy: a card is repainted on every catalog refresh, and destroying rows each time
            // costs a layout rebuild and a GC spike on a screen that is already instantiating cards.
            while (spawned.Count < lines.Count)
            {
                var row = Instantiate(rowPrefab, rowsParent);
                row.transform.localScale = Vector3.one;   // Instantiate(parent) keeps world scale — a scaled card would shrink its rows
                spawned.Add(row);
            }

            for (int i = 0; i < spawned.Count; i++)
            {
                var row = spawned[i];
                if (row == null) continue;
                bool used = i < lines.Count;
                if (row.gameObject.activeSelf != used) row.gameObject.SetActive(used);
                if (used) row.Setup(IconFor(lines[i]), lines[i].Amount);
            }
        }

        private void Summarise(List<RewardGrant> lines)
        {
            if (summaryTexts.Count == 0) return;
            var parts = new List<string>(lines.Count);
            foreach (var l in lines)
            {
                var amount = l.Amount.ToString(amountFormat, CultureInfo.InvariantCulture);
                parts.Add(l.Kind == RewardKind.Currency && !string.IsNullOrEmpty(l.Id) ? amount + " " + l.Id : amount);
            }
            var text = string.Join(summarySeparator, parts);
            foreach (var t in summaryTexts) if (t != null) t.text = text;
        }

        private void Clear()
        {
            foreach (var row in spawned) if (row != null && row.gameObject.activeSelf) row.gameObject.SetActive(false);
            foreach (var t in summaryTexts) if (t != null) t.text = "";
        }

        /// <summary>The icon for a line: its currency name, "Xp", or the chest/item id — matched the way RewardFly matches.</summary>
        private Sprite IconFor(RewardGrant line)
        {
            var id = line.Kind == RewardKind.Xp ? "Xp" : line.Id;
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (var entry in icons)
                if (entry != null && string.Equals(entry.rewardId, id, StringComparison.OrdinalIgnoreCase))
                    return entry.icon;
            return null;
        }
    }
}
