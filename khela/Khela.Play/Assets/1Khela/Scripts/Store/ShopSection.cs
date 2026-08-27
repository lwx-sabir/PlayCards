using System.Collections.Generic;
using System.Linq;
using Khela.Common.Store;
using TMPro;
using UnityEngine;

namespace PlayCard.Store
{
    /// <summary>
    /// One LANE of the shop — the chips ladder, the Kash ladder, the offers row — filled from the server's catalog.
    ///
    /// A card is already self-sufficient: drop a <see cref="StorePurchaseButton"/> on it, give it a product id, and it
    /// paints its own title, price, amount, art, badges, sale ribbon and buy state. This is the step above that: which
    /// products the lane shows, which card shape each one gets, and how many rows that needs.
    ///
    /// Two ways to run it, and the one you get depends on whether <see cref="rowPrefab"/> is assigned:
    /// <list type="bullet">
    /// <item><b>ROWS.</b> Give it a row prefab (a horizontal layout group) and a card prefab, and it builds
    /// ⌈products ÷ cardsPerRow⌉ rows and fills them left to right. The catalog decides the count, so a pack added in the
    /// admin lands in the right row without a scene edit, and a pack removed leaves no hole.</item>
    /// <item><b>AUTHORED.</b> Leave the row prefab empty and drag the cards you placed by hand into <see cref="cards"/>;
    /// they are bound in list order, surplus cards hide, and extras clone from the last one.</item>
    /// </list>
    ///
    /// <b>Filling a ragged row.</b> An offers lane has as many cards as there are offers — often one, in a row with room
    /// for three. <see cref="fillWith"/> names what completes it: further sections, each with its own card prefab, drawn
    /// in order until the row is full. So "the starter pack, then chips to fill the rest" is one lane, not two, and a
    /// week with three offers pushes the chips out on its own.
    ///
    /// It decides nothing about money. Which products exist, what they pay, what they cost and whether this player may
    /// buy them are all server answers; this only chooses which card carries which id.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShopSection : MonoBehaviour
    {
        /// <summary>A section to fall back on when the lane's own section does not fill its last row.</summary>
        [System.Serializable]
        public sealed class FillSource
        {
            [Tooltip("The server section to draw from: chips · kash · packs · vip …")]
            public string sectionKey;
            [Tooltip("The card shape these products get. Empty = the lane's main card prefab.")]
            public StorePurchaseButton cardPrefab;
        }

        [Header("Section")]
        [Tooltip("The server's section key: chips · kash · packs · vip · piggy · pass · daily. Must match the catalog.")]
        [SerializeField] private string sectionKey = "chips";

        [Header("Rows — assign these and the lane builds itself")]
        [Tooltip("One ROW: an empty object with a HorizontalLayoutGroup. Cards are spawned into it. Leave empty to run " +
                 "off the authored card list below instead.")]
        [SerializeField] private RectTransform rowPrefab;
        [Tooltip("The card spawned for this lane's own section — the chips card, the Kash card, the offer card.")]
        [SerializeField] private StorePurchaseButton cardPrefab;
        [Tooltip("Cards per row. 3 for the shop ladder.")]
        [SerializeField] private int cardsPerRow = 3;
        [Tooltip("Where rows go. Empty = this object (give it a VerticalLayoutGroup).")]
        [SerializeField] private RectTransform rowsParent;

        [Header("Fill the rest of the row")]
        [Tooltip("Only used in ROWS mode. When this lane's own section does not fill its last row, these sections are " +
                 "drawn in order to complete it — each with its own card shape. Leave empty for a lane that should end " +
                 "ragged (the chips ladder ends 3/3/1 and that is correct).")]
        [SerializeField] private List<FillSource> fillWith = new List<FillSource>();
        [Tooltip("Rows to show even when this lane's own section is EMPTY, so the fill sections still have somewhere to " +
                 "go. 1 keeps an offers lane alive as a row of chips on a week with no offer; 0 collapses it.")]
        [SerializeField] private int minRowsWhenFilling = 1;
        [Tooltip("Lanes ABOVE this one that have first claim on their products. Anything they are showing is skipped " +
                 "here, so the chips an offers row borrowed to finish itself do not appear again at the top of the " +
                 "chips ladder. How many they borrow changes with how many offers are live, so this is asked every " +
                 "refresh rather than being a fixed number of skips.")]
        [SerializeField] private List<ShopSection> continuesFrom = new List<ShopSection>();

        [Header("Authored cards — used only when there is no row prefab")]
        [Tooltip("Cards you placed by hand, in the order they should be filled. Each needs a StorePurchaseButton.")]
        [SerializeField] private List<StorePurchaseButton> cards = new List<StorePurchaseButton>();
        [Tooltip("Optional. Cloned when the section has more products than authored cards. Empty = clone the last card.")]
        [SerializeField] private StorePurchaseButton cardTemplate;
        [Tooltip("Where clones go. Empty = beside the card they were cloned from.")]
        [SerializeField] private RectTransform clonesParent;

        [Header("Limits")]
        [Tooltip("Never show more cards than this, however many products the sections have; 0 = no limit. A lane has the " +
                 "room its layout has, and a catalog edit should not be able to push a ladder off the screen.")]
        [SerializeField] private int maxCards = 12;

        [Header("Optional")]
        [Tooltip("The section's title from the catalog (\"Chips\", \"Kash\", \"VIP\"). Unassigned is ignored.")]
        [SerializeField] private List<TMP_Text> titleTexts = new List<TMP_Text>();
        [Tooltip("Shown when the lane has NOTHING to show — its section and every fill section came back empty.")]
        [SerializeField] private GameObject emptyRoot;
        [Tooltip("Shop_Row_Empty — SPAWNED into this lane when it has nothing to sell, and hidden again when it does. " +
                 "Prefer this over Empty Root: one prefab covers every lane, so a section added later gets the empty " +
                 "state for free instead of needing its own authored copy. Both can be set; both are honoured.")]
        [SerializeField] private RectTransform emptyPrefab;
        [SerializeField] private bool refreshOnEnable = true;

        /// <summary>One card the lane will show: the product, and the shape it gets.</summary>
        private struct Planned
        {
            public string ProductId;
            public StorePurchaseButton Prefab;
        }

        private readonly List<RectTransform> spawnedRows = new List<RectTransform>();
        /// <summary>Every spawned card in ROW-MAJOR order — the order the ladder reads in, and the order products bind in.</summary>
        private readonly List<StorePurchaseButton> spawnedCards = new List<StorePurchaseButton>();
        /// <summary>The prefab each spawned card came from, so a card can be replaced when its slot changes shape.</summary>
        private readonly List<StorePurchaseButton> spawnedFrom = new List<StorePurchaseButton>();
        /// <summary>Clones made in authored mode.</summary>
        private readonly List<StorePurchaseButton> clones = new List<StorePurchaseButton>();
        private bool warnedNoCard;
        /// <summary>Re-entrancy guard for <see cref="ClaimedProductIds"/> — two lanes referencing each other.</summary>
        private bool planning;

        private RectTransform spawnedEmpty;

        private bool RowMode => rowPrefab != null && cardPrefab != null;

        public string SectionKey => sectionKey;

        /// <summary>Retarget the lane at runtime (a shop that reuses one lane across tabs).</summary>
        public void SetSection(string key)
        {
            sectionKey = key ?? "";
            Refresh();
        }

        private void OnEnable()
        {
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed += HandleCatalogChanged;
            if (refreshOnEnable) Refresh();
        }

        private void OnDisable()
        {
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed -= HandleCatalogChanged;
        }

        private void HandleCatalogChanged(StoreCatalogDto _) => Refresh();

        public void Refresh()
        {
            var catalog = StoreCatalog.Instance;

            // The title is the catalog's, so renaming a lane in the admin renames it in the app.
            if (titleTexts.Count > 0)
            {
                var section = catalog?.Sections?.FirstOrDefault(s => s != null
                    && string.Equals(s.Key, sectionKey, System.StringComparison.OrdinalIgnoreCase));
                var title = section?.Title;
                if (!string.IsNullOrEmpty(title))
                    foreach (var t in titleTexts) if (t != null) t.text = title;
            }

            if (RowMode)
            {
                var plan = Plan();
                BuildRows(plan);
                // AFTER BuildRows: it instantiates rows into the same parent, and the empty state has to end up
                // below them rather than wherever the sibling order happened to leave it.
                ShowEmpty(plan.Count == 0);
                for (int i = 0; i < spawnedCards.Count; i++)
                    Bind(spawnedCards[i], i < plan.Count ? plan[i].ProductId : null, i < plan.Count);
                return;
            }

            var takenAbove = ClaimedByLanesAbove();
            var products = ProductsIn(sectionKey).Where(p => !takenAbove.Contains(p.Id)).ToList();
            if (maxCards > 0 && products.Count > maxCards) products = products.Take(maxCards).ToList();
            ShowEmpty(products.Count == 0);
            EnsureAuthoredCards(products.Count);

            int n = 0;
            foreach (var card in AuthoredCards())
            {
                if (card == null) continue;
                bool used = n < products.Count;
                Bind(card, used ? products[n].Id : null, used);
                n++;
            }
        }

        /// <summary>
        /// What this lane would show right now, product ids only — asked by the lanes BELOW it so they can skip what it
        /// has already claimed.
        ///
        /// Computed on demand rather than read from the last refresh: both lanes hear the same catalog change and the
        /// order they hear it in is not defined, so a cached answer would be last catalog's for whichever refreshed
        /// first. Planning is cheap (a handful of products), and this way the answer is never stale.
        /// </summary>
        public IEnumerable<string> ClaimedProductIds()
        {
            if (planning) yield break;   // two lanes pointing at each other must not recurse forever
            planning = true;
            try
            {
                if (RowMode)
                {
                    foreach (var p in Plan()) yield return p.ProductId;
                    yield break;
                }
                var own = ProductsIn(sectionKey);
                int cap = maxCards > 0 ? Mathf.Min(maxCards, own.Count) : own.Count;
                for (int i = 0; i < cap; i++) yield return own[i].Id;
            }
            finally { planning = false; }
        }

        private HashSet<string> ClaimedByLanesAbove()
        {
            var taken = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var lane in continuesFrom)
            {
                if (lane == null || lane == this) continue;
                foreach (var id in lane.ClaimedProductIds())
                    if (!string.IsNullOrEmpty(id)) taken.Add(id);
            }
            return taken;
        }

        private List<StoreProductDto> ProductsIn(string key)
        {
            var catalog = StoreCatalog.Instance;
            if (catalog == null || string.IsNullOrWhiteSpace(key)) return new List<StoreProductDto>();
            return catalog.InSection(key).Where(p => p != null).ToList();
        }

        /// <summary>
        /// What this lane shows, in order: its own section first, then the fill sections until the last row is full.
        ///
        /// The row count is set by the lane's OWN section, not by the fill — the fill exists to finish a row, never to
        /// add one. That is what keeps an offers lane an offers lane: three offers make a row of three, one offer makes
        /// a row of one offer and two chips, and the chips can never grow the lane on their own.
        /// </summary>
        private List<Planned> Plan()
        {
            int perRow = Mathf.Max(1, cardsPerRow);
            var plan = new List<Planned>();
            var taken = ClaimedByLanesAbove();

            foreach (var p in ProductsIn(sectionKey))
            {
                if (maxCards > 0 && plan.Count >= maxCards) break;
                if (taken.Contains(p.Id)) continue;
                plan.Add(new Planned { ProductId = p.Id, Prefab = cardPrefab });
            }

            bool canFill = fillWith.Any(f => f != null && !string.IsNullOrWhiteSpace(f.sectionKey));
            if (!canFill) return plan;

            int rows = Mathf.Max(Mathf.CeilToInt(plan.Count / (float)perRow), Mathf.Max(0, minRowsWhenFilling));
            int capacity = rows * perRow;
            if (maxCards > 0) capacity = Mathf.Min(capacity, maxCards);

            foreach (var source in fillWith)
            {
                if (plan.Count >= capacity) break;
                if (source == null || string.IsNullOrWhiteSpace(source.sectionKey)) continue;
                var prefab = source.cardPrefab != null ? source.cardPrefab : cardPrefab;
                foreach (var p in ProductsIn(source.sectionKey))
                {
                    if (plan.Count >= capacity) break;
                    if (taken.Contains(p.Id)) continue;
                    // A product already planned (a section listed twice, or its own section repeated in the fill) is
                    // skipped — the same pack twice in one row reads as a bug whatever put it there.
                    if (plan.Any(x => string.Equals(x.ProductId, p.Id, System.StringComparison.OrdinalIgnoreCase))) continue;
                    plan.Add(new Planned { ProductId = p.Id, Prefab = prefab });
                }
            }
            return plan;
        }

        /// <summary>Authored cards, then whatever this lane had to clone — the order products bind in.</summary>
        private IEnumerable<StorePurchaseButton> AuthoredCards()
        {
            foreach (var c in cards) yield return c;
            foreach (var c in clones) yield return c;
        }

        private void Bind(StorePurchaseButton card, string productId, bool show)
        {
            if (card == null) return;
            var go = card.gameObject;
            if (!show)
            {
                if (go.activeSelf) go.SetActive(false);
                return;
            }

            // SetProduct BEFORE the object goes active: the card refreshes itself in OnEnable, and binding after would
            // paint it once with the id it used to carry — a visible flash of the wrong price on every repaint.
            card.SetProduct(productId);
            if (!go.activeSelf) go.SetActive(true);
        }

        // ------------------------------------------------------------------ rows

        /// <summary>
        /// Build (and reuse) exactly the rows and cards the plan needs.
        ///
        /// Every row is filled to <see cref="cardsPerRow"/> and the spares are HIDDEN rather than left unspawned: a
        /// ragged last row still has its slots, so whatever the layout group does with them — centre them, stretch them,
        /// leave a gap — is a decision you make once in the row prefab, not something that moves with the number of
        /// products the admin happens to have enabled.
        /// </summary>
        /// <summary>
        /// The "nothing here yet" state for this lane.
        ///
        /// Kept alive once spawned rather than destroyed and rebuilt: a lane empties and fills as the admin enables
        /// products and as sales start and end, and that is a repaint, not a reason to churn a GameObject.
        /// </summary>
        private void ShowEmpty(bool empty)
        {
            if (emptyRoot != null) emptyRoot.SetActive(empty);
            if (emptyPrefab == null) return;

            if (!empty)
            {
                if (spawnedEmpty != null) spawnedEmpty.gameObject.SetActive(false);
                return;
            }

            if (spawnedEmpty == null)
            {
                var parent = rowsParent != null ? rowsParent
                           : clonesParent != null ? clonesParent
                           : (RectTransform)transform;
                try
                {
                    spawnedEmpty = Instantiate(emptyPrefab, parent, false);
                    spawnedEmpty.localScale = emptyPrefab.localScale;   // Instantiate rewrites scale under a scaled parent
                    spawnedEmpty.name = emptyPrefab.name;
                    Strip(spawnedEmpty);
                }
                catch (System.Exception ex)
                {
                    // A bad empty prefab must not take the lane down with it — and, because every lane hears the same
                    // catalog event, an exception escaping here stops the lanes AFTER this one from refreshing at all.
                    // One broken placeholder would empty the whole shop.
                    Debug.LogException(ex, this);
                    if (spawnedEmpty != null) Destroy(spawnedEmpty.gameObject);
                    spawnedEmpty = null;
                    return;
                }
            }
            spawnedEmpty.gameObject.SetActive(true);
            spawnedEmpty.SetAsLastSibling();
        }

        /// <summary>
        /// Make a spawned empty state INERT.
        ///
        /// These prefabs get made by duplicating a card and stripping it back, and a StorePurchaseButton left behind
        /// binds itself to whatever product id came with it — so the "nothing here" row comes up wearing a real pack's
        /// title. Disabling the component is too late: Instantiate runs Awake and OnEnable immediately, so the text has
        /// already been overwritten by the time anything here could run. It is destroyed, and the authored text is put
        /// back from the prefab itself.
        /// </summary>
        private void Strip(RectTransform instance)
        {
            var strays = instance.GetComponentsInChildren<StorePurchaseButton>(includeInactive: true);
            if (strays.Length == 0) return;

            foreach (var stray in strays)
            {
                if (stray == null) continue;
                Debug.LogWarning($"{name}: '{emptyPrefab.name}' still contains a StorePurchaseButton ('{stray.name}') — " +
                                 "it overwrote the placeholder's own text with a product's. Removed at runtime; take " +
                                 "the component off the prefab.", this);
                Destroy(stray);
            }

            // Same hierarchy, same order, so the prefab's own labels map one-to-one onto the clone's.
            var authored = emptyPrefab.GetComponentsInChildren<TMP_Text>(includeInactive: true);
            var live = instance.GetComponentsInChildren<TMP_Text>(includeInactive: true);
            for (int i = 0; i < live.Length && i < authored.Length; i++)
                if (live[i] != null && authored[i] != null) live[i].text = authored[i].text;
        }

        private void BuildRows(List<Planned> plan)
        {
            var parent = rowsParent != null ? rowsParent : (RectTransform)transform;
            int perRow = Mathf.Max(1, cardsPerRow);
            int rowsNeeded = plan.Count <= 0 ? 0 : Mathf.CeilToInt(plan.Count / (float)perRow);

            while (spawnedRows.Count < rowsNeeded)
            {
                var row = Instantiate(rowPrefab, parent, false);
                row.localScale = rowPrefab.localScale;
                row.name = $"Row_{spawnedRows.Count + 1}";
                spawnedRows.Add(row);

                for (int c = 0; c < perRow; c++)
                {
                    spawnedCards.Add(null);      // filled below, once the slot's shape is known
                    spawnedFrom.Add(null);
                }
            }

            for (int i = 0; i < spawnedCards.Count; i++)
            {
                var wanted = i < plan.Count ? plan[i].Prefab : null;
                if (wanted == null) continue;                 // an unused slot keeps whatever card it already has
                if (spawnedFrom[i] == wanted) continue;       // right shape already

                // The slot changed shape — an offer replaced by a chips pack, or the reverse. Prefabs differ, so the
                // card cannot be re-skinned; it is replaced in place. Only a catalog change gets here, so the cost of
                // destroying one card is paid once, not per repaint.
                if (spawnedCards[i] != null) Destroy(spawnedCards[i].gameObject);

                var row = spawnedRows[i / perRow];
                var card = Instantiate(wanted, row, false);
                card.transform.localScale = wanted.transform.localScale;
                card.transform.SetSiblingIndex(i % perRow);
                card.name = $"{wanted.name}_{i + 1}";
                spawnedCards[i] = card;
                spawnedFrom[i] = wanted;
            }

            // Rows past what the catalog needs are hidden, not destroyed — the catalog shrinks and grows (a sale SKU
            // appearing, a pack disabled), and churning objects on every refresh costs a layout rebuild each time.
            for (int r = 0; r < spawnedRows.Count; r++)
            {
                bool used = r < rowsNeeded;
                if (spawnedRows[r] != null && spawnedRows[r].gameObject.activeSelf != used)
                    spawnedRows[r].gameObject.SetActive(used);
            }
        }

        // ------------------------------------------------------------------ authored cards

        /// <summary>Clone up to <paramref name="needed"/> cards, reusing everything this lane already made.</summary>
        private void EnsureAuthoredCards(int needed)
        {
            int authored = cards.Count(c => c != null);
            int extra = needed - authored;
            if (extra <= 0) return;

            var source = cardTemplate != null ? cardTemplate : cards.LastOrDefault(c => c != null);
            if (source == null)
            {
                if (!warnedNoCard)
                {
                    warnedNoCard = true;
                    Debug.LogWarning($"[ShopSection] {name}: section '{sectionKey}' has {needed} product(s) and no card to " +
                                     "show them with — assign a row prefab + card prefab, or the authored cards.", this);
                }
                return;
            }

            // Transform, not RectTransform: a card whose parent is a plain Transform would otherwise cast to null and the
            // clone would land at the scene root, outside the layout it belongs to.
            Transform parent = clonesParent != null ? clonesParent : source.transform.parent;

            // After the LAST AUTHORED CARD that actually lives in this parent. Anchoring to `source` is wrong the moment
            // the template is not itself part of the row — a disabled template at the top of the layout — because the
            // clones would slide in FRONT of the authored cards while the binding still fills authored-first.
            int anchor = -1;
            foreach (var c in cards)
                if (c != null && c.transform.parent == parent)
                    anchor = Mathf.Max(anchor, c.transform.GetSiblingIndex());

            while (clones.Count < extra)
            {
                var clone = Instantiate(source, parent, false);
                clone.transform.localScale = source.transform.localScale;
                clone.name = $"{source.name} (catalog {clones.Count + 1})";
                if (anchor >= 0) clone.transform.SetSiblingIndex(anchor + 1 + clones.Count);
                clones.Add(clone);
            }
        }
    }
}
