using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PlayCard.Core;
using PlayCard.Game.Net;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Fills the wardrobe item grid for the selected category. Listens to the rail (<see cref="WardrobeTabBar.OnSelected"/>):
    /// when an OUTFIT tab is picked it pulls that tab's items from the shop catalog (merged tabs pull from all their
    /// slots via <see cref="AvatarConfig.SlotConfig.AllSlots"/>), spawns a <see cref="WardrobeItemCard"/> per item, and
    /// downloads each item's baked image. Tapping a card equips it on the live avatar. SHAPE tabs (shape:*) are ignored
    /// here — those are sliders, handled by the shape editor.
    ///
    /// The catalog is fetched once and cached; images are cached by SKU id. Everything is data-driven off the DB, so a
    /// tab is empty until items exist for its slot(s) in the cosmetics catalog.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeItemGrid : MonoBehaviour
    {
        [Tooltip("The card prefab (has a WardrobeItemCard).")]
        [SerializeField] private WardrobeItemCard cardPrefab;
        [Tooltip("Parent the cards spawn under — Item_Grid (the object with the Grid Layout Group).")]
        [SerializeField] private Transform grid;
        [Tooltip("The rail whose selection drives this grid.")]
        [SerializeField] private WardrobeTabBar rail;
        [Tooltip("The creator to equip onto when a card is tapped.")]
        [SerializeField] private AvatarCreator creator;
        [Tooltip("The rules asset — resolves merged tabs to their real slots.")]
        [SerializeField] private AvatarConfig config;
        [Tooltip("Optional: the static 'None' card that clears the current tab's slot(s). Shown only on outfit tabs.")]
        [SerializeField] private Button noneButton;
        [Tooltip("Optional: shown on the None card when nothing is equipped in the current tab (its selected look).")]
        [SerializeField] private GameObject noneSelectedState;
        [Tooltip("Optional: the whole item panel — hidden on shape tabs so the slider panel can take over.")]
        [SerializeField] private GameObject panelRoot;

        private List<CosmeticItemDto> _catalog;
        private readonly List<WardrobeItemCard> _cards = new List<WardrobeItemCard>();
        private readonly Dictionary<string, Sprite> _iconCache = new Dictionary<string, Sprite>();
        private WardrobeItemCard _selected;   // the highlighted (equipped) card in the current tab
        private string _currentKey;           // the tab currently shown (for the None button)
        private int _epoch;   // guards against a slow load painting a stale tab

        private void Awake() { if (noneButton != null) noneButton.onClick.AddListener(OnNoneClicked); }
        private void OnEnable() { if (rail != null) rail.OnSelected += OnTabSelected; }
        private void OnDisable() { if (rail != null) rail.OnSelected -= OnTabSelected; }

        private async void Start()
        {
            await EnsureCatalog();
            if (rail != null && !string.IsNullOrEmpty(rail.SelectedKey)) OnTabSelected(rail.SelectedKey);
        }

        private async Task EnsureCatalog()
        {
            if (_catalog != null) return;
            var r = await BlackjackRestClient.Instance.GetCosmeticsCatalogAsync();
            _catalog = r.Ok && r.Value?.Skus != null ? r.Value.Skus : new List<CosmeticItemDto>();
            if (!r.Ok) Debug.LogWarning($"[WardrobeItemGrid] catalog fetch failed: {r.Error}");
        }

        private async void OnTabSelected(string key)
        {
            int epoch = ++_epoch;
            _currentKey = key;
            Clear();

            // The item panel shows for item/set tabs; the slider panel takes over on shape tabs.
            bool isShape = !string.IsNullOrEmpty(key) && key.StartsWith(WardrobeTabBar.ShapePrefix);
            if (panelRoot != null) panelRoot.SetActive(!isShape);

            // The "None" (clear slot) card only makes sense on an outfit slot tab — hide it on shape/sets tabs.
            bool isOutfitSlot = !isShape && key != WardrobeTabBar.SetsKey;
            if (noneButton != null) noneButton.gameObject.SetActive(isOutfitSlot);

            // Shape tabs are sliders, not items — the shape editor handles those.
            if (string.IsNullOrEmpty(key) || key.StartsWith(WardrobeTabBar.ShapePrefix)) return;

            await EnsureCatalog();
            if (epoch != _epoch) return;   // a newer tab was picked while we awaited

            List<CosmeticItemDto> items;
            if (key == WardrobeTabBar.SetsKey)
            {
                items = _catalog.Where(i => i != null && i.Type == "set").ToList();   // full costumes
            }
            else
            {
                var slots = SlotsForTab(key);
                items = _catalog.Where(i => i != null && i.Type == "item"
                                            && !string.IsNullOrEmpty(i.Slot) && slots.Contains(i.Slot)).ToList();
            }

            foreach (var item in items)
            {
                var card = Instantiate(cardPrefab, grid);
                card.Bind(item, OnItemClicked);
                _cards.Add(card);
                // Highlight whichever item is already equipped in its slot when the tab opens (items only — sets have no slot).
                if (creator != null && !string.IsNullOrEmpty(item.Slot) && !string.IsNullOrEmpty(item.Path)
                    && string.Equals(item.Path, creator.CurrentPartPath(item.Slot), StringComparison.OrdinalIgnoreCase))
                {
                    card.SetSelected(true);
                    _selected = card;
                }
                LoadIcon(item, card, epoch);
            }
            UpdateNoneState();
        }

        // A tab's real slots: its own key plus any merged extras (Underwear = UnderLower + UnderUpper, etc.).
        private HashSet<string> SlotsForTab(string key)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
            var sc = config?.slots?.FirstOrDefault(s => s != null && s.slot == key);
            if (sc != null) foreach (var s in sc.AllSlots()) set.Add(s);
            return set;
        }

        // Equip the tapped item on the live avatar (self-attaches + merges) and move the selected frame to it.
        // Buy/colour flow comes later.
        private void OnItemClicked(WardrobeItemCard card)
        {
            if (card?.Item == null) return;
            if (creator != null)
            {
                if (card.Item.Type == "set") EquipSet(card.Item);
                else if (!string.IsNullOrEmpty(card.Item.Path)) EquipItem(card.Item.Path);
            }

            if (_selected != null && _selected != card) _selected.SetSelected(false);
            card.SetSelected(true);
            _selected = card;
            UpdateNoneState();
        }

        // A set is a PREVIEW. While one is active, _preSet[slot] = the PLAYER'S own item that was in that slot before the
        // set overlaid it (empty string = the slot was empty). Kept in DATA so we never re-read the rig mid-operation
        // (BoZo attaches deferred — reading the rig right after a change gives the OLD state).
        private readonly Dictionary<string, string> _preSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The player's real item for a slot: the remembered pre-set value if a set covers it, else what's on the rig.
        private string PlayerItemFor(string slot)
            => _preSet.TryGetValue(slot, out var p) ? p : creator.CurrentPartPath(slot);

        // Restore the active set's slots to the player's own items, EXCEPT those in `keep` (the new selection fills them).
        // All removes/adds here happen before the caller's own adds, so there's no fragile remove-after-add.
        private void RestoreSetExcept(HashSet<string> keep)
        {
            foreach (var kv in _preSet)
            {
                if (keep != null && keep.Contains(kv.Key)) continue;
                if (string.IsNullOrEmpty(kv.Value)) creator.RemoveOutfitSlot(kv.Key);
                else creator.SetOutfit(kv.Value);
            }
            _preSet.Clear();
        }

        // The slots a set fills — each piece's slot plus its leg conflict (a Bottom/Leggings piece also owns Overall, etc.).
        private HashSet<string> SetSlots(CosmeticItemDto set)
        {
            var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in set.Pieces)
                if (p != null && !string.IsNullOrEmpty(p.Path))
                {
                    string s = p.Path.Split('/')[0];
                    slots.Add(s);
                    foreach (var c in ConflictSlots(s)) slots.Add(c);
                }
            return slots;
        }

        // Equip one garment: the item's slot + its leg conflict get the item; every other set slot reverts to the player's.
        private void EquipItem(string path)
        {
            string slot = path.Split('/')[0];
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { slot };
            foreach (var c in ConflictSlots(slot)) owned.Add(c);

            RestoreSetExcept(owned);
            foreach (var c in ConflictSlots(slot)) creator.RemoveOutfitSlot(c);
            creator.SetOutfit(path);
        }

        // Only the LEG conflict: a full-body Overall covers the legs, so it can't coexist with a Bottom/Leggings. Top is
        // deliberately NOT here — dungarees-style overalls are worn over a shirt; BoZo's own hide-tags handle any top overlap.
        private static readonly Dictionary<string, string[]> Conflicts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Overall",  new[] { "Bottom", "Leggings" } },
            { "Bottom",   new[] { "Overall" } },
            { "Leggings", new[] { "Overall" } },
        };

        private static IEnumerable<string> ConflictSlots(string slot)
            => Conflicts.TryGetValue(slot, out var c) ? c : Array.Empty<string>();

        // Equip a costume: its slots get the set; the previous set's other slots revert to the player's items. The new
        // pre-set snapshot uses the PLAYER'S items (old memory where it existed, else the rig) — never the old set's pieces.
        private void EquipSet(CosmeticItemDto set)
        {
            var slots = SetSlots(set);

            var newPre = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in slots) newPre[s] = PlayerItemFor(s);

            RestoreSetExcept(slots);                                     // revert the old set's OTHER slots to the player
            foreach (var s in slots) creator.RemoveOutfitSlot(s);        // clear this set's slots
            foreach (var p in set.Pieces)
                if (p != null && !string.IsNullOrEmpty(p.Path)) creator.SetOutfit(p.Path);
            foreach (var kv in newPre) _preSet[kv.Key] = kv.Value;       // remember the player's items under this set
        }

        // The "None" card: strip whatever's equipped in the current tab's slot(s) (all merged slots, e.g. bra + underwear).
        private void OnNoneClicked()
        {
            if (creator == null || string.IsNullOrEmpty(_currentKey)) return;
            if (_currentKey.StartsWith(WardrobeTabBar.ShapePrefix) || _currentKey == WardrobeTabBar.SetsKey) return;

            // Leaving a set: revert its other slots to the player's items, then clear this tab's slot(s).
            var owned = SlotsForTab(_currentKey);
            RestoreSetExcept(owned);
            foreach (var slot in owned) creator.RemoveOutfitSlot(slot);
            if (_selected != null) { _selected.SetSelected(false); _selected = null; }
            UpdateNoneState();
        }

        // None reads as selected whenever nothing in this tab is equipped.
        private void UpdateNoneState()
        {
            if (noneSelectedState != null) noneSelectedState.SetActive(_selected == null);
        }

        private void Clear()
        {
            foreach (var c in _cards) if (c != null) Destroy(c.gameObject);
            _cards.Clear();
            _selected = null;
        }

        private async void LoadIcon(CosmeticItemDto item, WardrobeItemCard card, int epoch)
        {
            if (!item.HasIcon) return;
            var sprite = await GetIcon(item.Id);
            if (sprite != null && card != null && epoch == _epoch) card.SetImage(sprite);
        }

        private async Task<Sprite> GetIcon(string id)
        {
            if (_iconCache.TryGetValue(id, out var cached)) return cached;
            string url = $"{AppConfig.Instance.BaseApiUrl}/api/shop/cosmetics/{id}/icon";

            // Download raw PNG bytes, then decode into a texture WE own — a DownloadHandlerTexture's texture gets
            // destroyed when the request is disposed, leaving the sprite blank.
            byte[] data;
            using (var req = UnityWebRequest.Get(url))
            {
                var tcs = new TaskCompletionSource<bool>();
                req.SendWebRequest().completed += _ => tcs.TrySetResult(true);
                await tcs.Task;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[WardrobeItemGrid] icon '{id}' failed: {(long)req.responseCode} {req.error}");
                    return null;
                }
                data = req.downloadHandler.data;
            }
            if (data == null || data.Length == 0) return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(data)) return null;   // decodes the PNG
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            _iconCache[id] = sprite;
            return sprite;
        }
    }
}
