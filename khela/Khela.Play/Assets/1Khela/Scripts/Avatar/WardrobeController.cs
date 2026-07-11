using System.Collections.Generic;
using PlayCard.App;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    using ShapeLimit = AvatarConfig.ShapeLimit;
    using ShapeCategory = AvatarConfig.ShapeCategory;

    /// <summary>
    /// The curated wardrobe HUD, bound to <see cref="AvatarCreator"/>. Opens on the player's SAVED avatar (full diff —
    /// shapes + outfits + colours), lets them edit within the config limits on ONE live actor (the safe single-merge
    /// path), and on Save persists the full <see cref="AvatarData"/> to the server and returns Home. Back discards
    /// (edits were on the in-memory actor only). Re-entrant from Home — distinct from onboarding's pick-only flow.
    ///
    /// Prefab contracts (all built at runtime from the creator's API):
    ///  • sliderTilePrefab — has a <see cref="Slider"/> (0..1) + a <see cref="TMP_Text"/> label.
    ///  • partTilePrefab / swatchTilePrefab / slotTabPrefab — have a <see cref="Button"/>; an <see cref="Image"/> named
    ///    "Icon" (else the first child Image) for the picture/colour; an optional <see cref="TMP_Text"/> label; and an
    ///    optional child named "Selected" toggled for highlight (falls back to tinting the Button's target graphic).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeController : MonoBehaviour
    {
        [Header("Engine")]
        [SerializeField] private AvatarCreator creator;

        [Header("Shape sliders")]
        [SerializeField] private Transform sliderContainer;
        [SerializeField] private GameObject sliderTilePrefab;
        [Tooltip("Optional: auto-builds Body / Face / Shape category tabs here (reuse the Slot Tab prefab). Empty = all shapes in one flat list.")]
        [SerializeField] private Transform shapeTabContainer;

        [Header("Outfits")]
        [SerializeField] private Transform slotTabContainer;
        [SerializeField] private GameObject slotTabPrefab;
        [SerializeField] private Transform partContainer;
        [SerializeField] private GameObject partTilePrefab;
        [Tooltip("Show a leading 'None' tile that clears the slot.")]
        [SerializeField] private bool showClearTile = true;

        [Header("Colours")]
        [Tooltip("Optional: auto-builds one tab per palette (Skin/Hair/…) here (reuse the Slot Tab prefab). Empty = all swatches shown together.")]
        [SerializeField] private Transform paletteTabContainer;
        [SerializeField] private Transform paletteContainer;
        [SerializeField] private GameObject swatchTilePrefab;

        [Header("Actions")]
        [SerializeField] private Button saveButton, backButton;
        [SerializeField] private TMP_Text statusLabel;
        [Tooltip("Shown while the creator is loading (bound to creator.IsBusy).")]
        [SerializeField] private GameObject busyOverlay;

        [Header("Fallback (player with no saved avatar)")]
        [SerializeField] private string fallbackBaseId = "Base/1DefaultMale";

        private static readonly Color Selected = new Color(0.20f, 0.65f, 1f, 1f);
        private static readonly Color RiskTint = new Color(1f, 0.75f, 0.35f, 1f);

        private ShapeCategory? _category;
        private string _slot;
        private int _paletteIndex;
        private bool _busy;
        private readonly List<(string path, GameObject tile)> _partTiles = new List<(string, GameObject)>();

        private async void Start()
        {
            if (creator == null) { Debug.LogError("[Wardrobe] Assign the AvatarCreator."); return; }

            if (saveButton != null) saveButton.onClick.AddListener(OnSave);
            if (backButton != null) backButton.onClick.AddListener(() => SceneNavigator.GoToHome());

            creator.OnAvatarChanged += RebuildAll;

            var fbGender = creator.Genders().Count > 0 ? creator.Genders()[0] : AvatarConfig.Gender.Male;
            SetBusy(true);
            await creator.LoadSavedOrBaseAsync(fbGender, fallbackBaseId);   // fires OnAvatarChanged → RebuildAll
            SetBusy(false);
        }

        private void OnDestroy()
        {
            if (creator != null) creator.OnAvatarChanged -= RebuildAll;
        }

        // ---- wholesale rebuild (after a load) ----

        private void RebuildAll()
        {
            BuildShapeTabs();
            BuildSliders();
            BuildSlotTabs();
            if (string.IsNullOrEmpty(_slot))
            {
                var slots = creator.Slots();
                _slot = slots.Count > 0 ? slots[0] : null;
            }
            BuildParts();
            BuildPaletteTabs();
            BuildSwatches();
        }

        // ---- shape category tabs ----

        private static readonly ShapeCategory[] Categories = { ShapeCategory.Body, ShapeCategory.Face, ShapeCategory.BodyMod };
        private static string CategoryLabel(ShapeCategory c) => c == ShapeCategory.BodyMod ? "Shape" : c.ToString();

        private void BuildShapeTabs()
        {
            if (shapeTabContainer == null || slotTabPrefab == null) { _category = null; return; }   // no tabs → all shapes flat
            Clear(shapeTabContainer);
            bool picked = false;
            foreach (var cat in Categories)
            {
                if (creator.EditableShapes(cat).Count == 0) continue;
                if (!picked) { _category = cat; picked = true; }   // default to the first non-empty category
                var go = Instantiate(slotTabPrefab, shapeTabContainer, false);
                var label = go.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = CategoryLabel(cat);
                var btn = go.GetComponentInChildren<Button>();
                var captured = cat;
                if (btn != null) btn.onClick.AddListener(() => { _category = captured; BuildSliders(); });
            }
            if (!picked) _category = null;
        }

        // ---- shape sliders ----

        private void BuildSliders()
        {
            if (sliderContainer == null || sliderTilePrefab == null) return;
            Clear(sliderContainer);
            foreach (var s in creator.EditableShapes(_category))
            {
                var go = Instantiate(sliderTilePrefab, sliderContainer, false);
                var label = go.GetComponentInChildren<TMP_Text>();
                if (label != null) { label.text = s.label; if (s.deformRisk) label.color = RiskTint; }
                var slider = go.GetComponentInChildren<Slider>();
                if (slider != null)
                {
                    slider.minValue = 0f; slider.maxValue = 1f;
                    slider.SetValueWithoutNotify(creator.GetNormalized(s));
                    var captured = s;   // per-iteration binding is fine on C#5+, explicit for clarity
                    slider.onValueChanged.AddListener(t => creator.SetNormalized(captured, t));
                }
            }
        }

        // ---- outfits ----

        private void BuildSlotTabs()
        {
            if (slotTabContainer == null || slotTabPrefab == null) return;
            Clear(slotTabContainer);
            foreach (var slot in creator.Slots())
            {
                var go = Instantiate(slotTabPrefab, slotTabContainer, false);
                var label = go.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = creator.Config != null ? creator.Config.SlotLabel(slot) : slot;
                var btn = go.GetComponentInChildren<Button>();
                var captured = slot;
                if (btn != null) btn.onClick.AddListener(() => { _slot = captured; BuildParts(); });
            }
        }

        private void BuildParts()
        {
            if (partContainer == null || partTilePrefab == null || string.IsNullOrEmpty(_slot)) return;
            Clear(partContainer);
            _partTiles.Clear();

            if (showClearTile)
            {
                var none = Instantiate(partTilePrefab, partContainer, false);
                SetTile(none, "None", null);
                var b = none.GetComponentInChildren<Button>();
                if (b != null) b.onClick.AddListener(() => { creator.RemoveOutfitSlot(_slot); HighlightParts(); });
                _partTiles.Add((null, none));
            }

            foreach (var opt in creator.PartsForSlot(_slot))
            {
                var go = Instantiate(partTilePrefab, partContainer, false);
                SetTile(go, opt.Label, opt.Icon);
                var btn = go.GetComponentInChildren<Button>();
                var captured = opt.Path;
                if (btn != null) btn.onClick.AddListener(() => { creator.SetOutfit(captured); HighlightParts(); });
                _partTiles.Add((opt.Path, go));
            }
            HighlightParts();
        }

        private void HighlightParts()
        {
            string current = creator.CurrentPartPath(_slot);   // null when the slot is empty → the "None" tile
            foreach (var (path, tile) in _partTiles)
                SetHighlight(tile, path == current);
        }

        // ---- colours ----

        private void BuildPaletteTabs()
        {
            if (paletteTabContainer == null || slotTabPrefab == null) return;
            Clear(paletteTabContainer);
            var palettes = creator.Palettes;
            for (int i = 0; i < palettes.Count; i++)
            {
                var go = Instantiate(slotTabPrefab, paletteTabContainer, false);
                var label = go.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = palettes[i].target;
                var btn = go.GetComponentInChildren<Button>();
                int captured = i;
                if (btn != null) btn.onClick.AddListener(() => { _paletteIndex = captured; BuildSwatches(); });
            }
            if (_paletteIndex >= palettes.Count) _paletteIndex = 0;
        }

        private void BuildSwatches()
        {
            if (paletteContainer == null || swatchTilePrefab == null) return;
            Clear(paletteContainer);
            var palettes = creator.Palettes;
            if (palettes.Count == 0) return;

            // With a palette-tab bar → show only the selected palette; without one → all palettes' swatches together.
            var show = new List<AvatarConfig.ColorPalette>();
            if (paletteTabContainer != null) show.Add(palettes[Mathf.Clamp(_paletteIndex, 0, palettes.Count - 1)]);
            else show.AddRange(palettes);

            foreach (var palette in show)
            {
                if (palette == null || palette.swatches == null) continue;
                var current = creator.CurrentPaletteColor(palette);
                foreach (var swatch in palette.swatches)
                {
                    var go = Instantiate(swatchTilePrefab, paletteContainer, false);
                    var icon = FindIcon(go);
                    if (icon != null) icon.color = swatch;
                    var btn = go.GetComponentInChildren<Button>();
                    var capPalette = palette; var capSwatch = swatch;
                    if (btn != null) btn.onClick.AddListener(() => creator.ApplyPalette(capPalette, capSwatch));
                    SetHighlight(go, current.HasValue && Approximately(current.Value, swatch), allowTint: false);
                }
            }
        }

        // ---- save / busy ----

        private async void OnSave()
        {
            if (_busy || creator == null) return;
            if (creator.SaveWouldClobber)   // stored avatar didn't load — never overwrite it with the fallback base
            {
                Status("Couldn't load your saved avatar. Go back and reopen — not overwriting it.");
                return;
            }
            _busy = true;
            SetButtons(false);
            Status("Saving…");
            bool ok = await creator.SaveAsync();
            if (ok) { SceneNavigator.GoToHome(); return; }   // Home's stage re-renders via AvatarService.MineChanged
            Status("Couldn't save — try again.");
            _busy = false;
            SetButtons(true);
        }

        private void SetBusy(bool on) { if (busyOverlay != null) busyOverlay.SetActive(on); }
        private void SetButtons(bool on)
        {
            if (saveButton != null) saveButton.interactable = on;
            if (backButton != null) backButton.interactable = on;
        }
        private void Status(string msg) { if (statusLabel != null) statusLabel.text = msg; }

        // ---- tile helpers ----

        private static void SetTile(GameObject tile, string label, Sprite icon)
        {
            var text = tile.GetComponentInChildren<TMP_Text>();
            if (text != null) text.text = label;
            var img = FindIcon(tile);
            if (img != null)
            {
                img.sprite = icon;
                img.enabled = icon != null || text == null;   // hide an empty icon if there's a text label
                img.color = Color.white;
            }
        }

        // Icon = a child Image named "Icon", else the first Image that isn't the Button's own graphic.
        private static Image FindIcon(GameObject tile)
        {
            var named = tile.transform.Find("Icon");
            if (named != null && named.TryGetComponent(out Image ni)) return ni;
            var btn = tile.GetComponentInChildren<Button>();
            foreach (var img in tile.GetComponentsInChildren<Image>(true))
                if (btn == null || img != btn.targetGraphic) return img;
            return tile.GetComponentInChildren<Image>(true);
        }

        // Highlight = a child named "Selected" toggled, else tint the Button's target graphic.
        private static void SetHighlight(GameObject tile, bool on, bool allowTint = true)
        {
            var sel = tile.transform.Find("Selected");
            if (sel != null) { sel.gameObject.SetActive(on); return; }
            if (!allowTint) return;   // swatch tiles: the graphic IS the colour — never tint it, use a "Selected" child
            var btn = tile.GetComponentInChildren<Button>();
            if (btn != null && btn.targetGraphic != null) btn.targetGraphic.color = on ? Selected : Color.white;
        }

        private static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) Destroy(t.GetChild(i).gameObject);
        }

        private static bool Approximately(Color a, Color b)
            => Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f;
    }
}
