using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Khela.Common.Pass;
using Khela.Common.Rewards;
using PlayCard.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Pass
{
    /// <summary>
    /// Binds the Monthly Pass panel to a server snapshot: builds the two reward rows plus the day markers, keeps the
    /// countdown ticking, and turns a card tap into an intent (claim / watch ads / subscribe).
    ///
    /// It renders ONLY what the server said. Which days are claimable, which are locked, which cost ad views and how
    /// long the cycle has left are all decisions already made in <c>PassService</c> — nothing here recomputes them
    /// from the device clock, which is what keeps a changed phone date from unlocking anything.
    ///
    /// Fetching is not this class's job: hand it a <see cref="PassStateDto"/> via <see cref="Render"/> and subscribe
    /// to <see cref="ClaimRequested"/> / <see cref="SubscribeRequested"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PassScreen : MonoBehaviour
    {
        [Header("Rows — the three parallel, index-aligned lists")]
        [Tooltip("Item container under GoldenPass. Day cards are spawned here.")]
        [SerializeField] private RectTransform goldenRow;
        [Tooltip("Item container under FreePass.")]
        [SerializeField] private RectTransform freeRow;
        [Tooltip("Container for the numbered day markers (under Pass_Node).")]
        [SerializeField] private RectTransform markerRow;
        [SerializeField] private ScrollRect scrollRect;
        [Tooltip("Cycle progress as a Slider (SliderBar). If the bar is a Slider, assign it HERE, not as an Image — " +
                 "a Slider rewrites its fill image's fillAmount from its own value and would overwrite us.")]
        [SerializeField] private Slider progressSlider;
        [Tooltip("Cycle progress as a plain filled Image (only when there is no Slider).")]
        [SerializeField] private Image progressFill;
        [Tooltip("Stretch the bar to the day markers' width each build, so the fill lines up with the right day " +
                 "however many days the month has. Turn off only if the bar is deliberately a different length.")]
        [SerializeField] private bool matchProgressBarToMarkers = true;

        [Header("Per-reward card art — optional, checked before the generic variants")]
        [Tooltip("A single-reward day whose reward matches this id uses this prefab. Id is the reward's own: " +
                 "\"Chips\", \"Kash\", \"Gems\", a chest key, an item key. Leave the list empty to use the generic variants.")]
        [SerializeField] private CardVariant[] goldenByReward;
        [SerializeField] private CardVariant[] freeByReward;

        [Header("Golden card variants — leave any empty to fall back to the single-item one")]
        [SerializeField] private PassCardView goldenOneItem;
        [SerializeField] private PassCardView goldenTwoItems;
        [Tooltip("Milestone days (the server flags them).")]
        [SerializeField] private PassCardView goldenMilestone;
        [Tooltip("Used while a collectible day is UNCOLLECTED; once taken it respawns as the plain variant.")]
        [SerializeField] private PassCardView goldenCollectible;
        [SerializeField] private PassCardView goldenCollectibleTwoItems;

        [Header("Free card variants")]
        [SerializeField] private PassCardView freeOneItem;
        [SerializeField] private PassCardView freeCollectible;

        [Header("Day marker")]
        [Tooltip("Optional numbered marker prefab; leave empty until it exists.")]
        [SerializeField] private PassDayMarkerView markerPrefab;

        [Header("Header")]
        [SerializeField] private TMP_Text titleText;
        [Tooltip("\"Ends in 12d 4h\" — counts down to the end of the player's own month.")]
        [SerializeField] private TMP_Text endsInText;
        [Tooltip("Optional \"next day in HH:MM:SS\" label.")]
        [SerializeField] private TMP_Text nextDayText;
        [Tooltip("Subscribe / Activate button. Hidden while the player is already Golden.")]
        [SerializeField] private Button activateButton;
        [Tooltip("Optional \"unlock N missed days\" label on or near the Activate button.")]
        [SerializeField] private TMP_Text missedDaysText;
        [SerializeField] private Button closeButton;

        /// <summary>Art for one specific reward — the Kash frame, the chip frame, a ticket frame.</summary>
        [Serializable]
        public sealed class CardVariant
        {
            [Tooltip("The reward's id, exactly as the server sends it: Chips / Kash / Gems / a chest or item key.")]
            public string rewardId;
            public PassCardView prefab;
            [Tooltip("Use this only on milestone days (leave off to use it on any day paying that reward).")]
            public bool milestoneOnly;
        }

        /// <summary>A day the player wants. <c>useAds</c> means they're spending rewarded-ad credits on a missed day.</summary>
        public event Action<int, bool> ClaimRequested;

        /// <summary>The player tapped Activate, or a Golden-locked day.</summary>
        public event Action SubscribeRequested;

        private readonly List<PassCardView> _goldenCards = new List<PassCardView>();
        private readonly List<PassCardView> _freeCards = new List<PassCardView>();
        private readonly List<PassDayMarkerView> _markers = new List<PassDayMarkerView>();

        private PassStateDto _state;
        private Coroutine _ticker;

        private void Awake()
        {
            if (activateButton != null) activateButton.onClick.AddListener(() => SubscribeRequested?.Invoke());
            if (closeButton != null) closeButton.onClick.AddListener(Hide);
            if (scrollRect != null) scrollRect.onValueChanged.AddListener(_ => CullOffscreenFx());
        }

        /// <summary>
        /// Hide the particle FX of cards outside the viewport.
        ///
        /// A RectMask2D clips UI graphics through the shader's clip rect; a ParticleSystemRenderer isn't a UI graphic
        /// and has no such shader, so sparkles from a card scrolled off the ladder still draw over the rest of the
        /// screen. Clipping them by hand is the only fix that doesn't need a UI-particle package or sprite-sheet FX.
        /// </summary>
        private void CullOffscreenFx()
        {
            var viewport = scrollRect != null ? (scrollRect.viewport != null ? scrollRect.viewport : scrollRect.transform as RectTransform) : null;
            if (viewport == null) return;

            var bounds = WorldRect(viewport);
            Cull(_goldenCards, bounds);
            Cull(_freeCards, bounds);

            // Decoration authored inside the scrolling content (on the rows or the node bar) has the same problem.
            // Cards are deliberately EXCLUDED: they own their FX above, and letting this sweep touch them would undo
            // that decision — it ran second and used a looser test, so it was switching card sparkles back on.
            if (_contentFx == null && scrollRect.content != null)
            {
                _contentFx = scrollRect.content.GetComponentsInChildren<ParticleSystemRenderer>(true)
                    .Where(r => r != null && r.GetComponentInParent<PassCardView>() == null)
                    .ToArray();
            }

            if (_contentFx == null) return;
            for (int i = 0; i < _contentFx.Length; i++)
            {
                var renderer = _contentFx[i];
                if (renderer == null) continue;

                var b = renderer.bounds;
                bool inside = b.min.x >= bounds.xMin && b.max.x <= bounds.xMax &&
                              b.min.y >= bounds.yMin && b.max.y <= bounds.yMax;
                if (renderer.enabled != inside) renderer.enabled = inside;
            }
        }

        private ParticleSystemRenderer[] _contentFx;

        private static void Cull(List<PassCardView> cards, Rect bounds)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card != null) card.CullFxTo(bounds);
            }
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        private static Rect WorldRect(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Rect.MinMaxRect(Corners[0].x, Corners[0].y, Corners[2].x, Corners[2].y);
        }

        private void OnDisable()
        {
            if (_ticker != null) { StopCoroutine(_ticker); _ticker = null; }
        }

        /// <summary>Show the panel with a snapshot. Safe to call again with a fresh one after a claim.</summary>
        public void Render(PassStateDto state)
        {
            // Rendering a PREFAB ASSET instead of an instance spawns 60+ objects into the asset's own transforms and
            // silently draws nothing. Fail once, loudly, instead of a wall of Unity warnings.
            if (!gameObject.scene.IsValid())
            {
                Debug.LogError($"{name}: PassScreen.Render was called on a PREFAB ASSET. Instantiate the pass prefab " +
                               "first (PassButton does this) — a prefab asset can't be shown.", this);
                return;
            }

            _state = state;
            gameObject.SetActive(true);

            if (state == null || !state.Active)
            {
                // No program, no cycle, or the pass is switched off — show nothing rather than an empty ladder.
                Clear();
                if (titleText != null) titleText.text = string.Empty;
                if (endsInText != null) endsInText.text = string.Empty;
                if (activateButton != null) activateButton.gameObject.SetActive(false);
                return;
            }

            BuildHeader(state);
            BuildLadder(state);
            ScrollToToday(state);

            if (_ticker != null) StopCoroutine(_ticker);
            if (isActiveAndEnabled) _ticker = StartCoroutine(Tick());   // a disabled object can't host the countdown
        }

        /// <summary>Update one day in place after a claim, without rebuilding the ladder (keeps the tween alive).
        /// The card is respawned only when the variant itself has to change — a collected collectible.</summary>
        public void MarkClaimed(int day)
        {
            var node = _state?.Nodes?.FirstOrDefault(n => n.Index == day);
            if (node == null) return;

            node.Claimed = true;
            node.ClaimableNow = false;
            node.AdUnlockable = false;
            node.GoldenLocked = false;

            // The card that was just collected was showing the COLLECTIBLE variant, so it has to be respawned as the
            // plain one with Collected on — a state flip alone would leave the sparkles up on a taken day.
            Render(_state);

            var marker = _markers.FirstOrDefault(m => m != null && m.Day == day);
            if (marker != null) marker.Bind(day, PassDayMarkerState.Past);
        }

        public void Hide() => gameObject.SetActive(false);

        // ---------------- header ----------------

        private void BuildHeader(PassStateDto state)
        {
            if (titleText != null) titleText.text = string.IsNullOrEmpty(state.Title) ? "Monthly Pass" : state.Title;

            if (activateButton != null) activateButton.gameObject.SetActive(!state.IsGolden);

            if (missedDaysText != null)
            {
                // The conversion line: how many days the subscription would hand back right now.
                bool show = !state.IsGolden && state.GoldenLockedCount > 0;
                missedDaysText.gameObject.SetActive(show);
                if (show)
                    missedDaysText.text = state.GoldenLockedCount == 1
                        ? "Unlock 1 missed day"
                        : $"Unlock {state.GoldenLockedCount} missed days";
            }

            float progress = state.Days > 0 ? Mathf.Clamp01((float)state.DayIndex / state.Days) : 0f;

            // A Slider OWNS its fill image: UpdateVisuals() rewrites fillAmount from `value` every time it runs, so
            // writing the image directly is silently undone the same frame. If the progress bar is a Slider — even
            // when only its fill Image was wired up — drive the SLIDER.
            var slider = ResolveProgressSlider();
            if (slider != null)
            {
                slider.minValue = 0f;
                slider.maxValue = 1f;
                // A progress bar is a READOUT: left interactable, a drag across it moves the value and the ladder
                // would show a day the player isn't on. Also stop it eating drags meant for the ScrollRect.
                slider.interactable = false;
                if (slider.targetGraphic != null) slider.targetGraphic.raycastTarget = false;
                slider.SetValueWithoutNotify(progress);
            }
            else if (progressFill != null)
            {
                progressFill.fillAmount = progress;
            }
        }

        /// <summary>The Slider driving the bar: the one assigned, or the one that owns the assigned fill image.
        /// Resolved once — a fill Image inside a Slider is the normal way this art is built.</summary>
        private Slider ResolveProgressSlider()
        {
            if (progressSlider != null) return progressSlider;
            if (_resolvedSlider != null || _sliderResolved) return _resolvedSlider;

            _sliderResolved = true;
            if (progressFill != null) _resolvedSlider = progressFill.GetComponentInParent<Slider>(true);
            return _resolvedSlider;
        }

        private Slider _resolvedSlider;
        private bool _sliderResolved;

        private IEnumerator Tick()
        {
            var wait = new WaitForSeconds(1f);
            while (_state != null && _state.Active)
            {
                var now = DateTime.UtcNow;
                if (endsInText != null) endsInText.text = FormatSpan(_state.CycleEndUtc - now, longForm: true);
                if (nextDayText != null) nextDayText.text = FormatSpan(_state.NextDayUtc - now, longForm: false);
                yield return wait;
            }
        }

        // ---------------- ladder ----------------

        private void BuildLadder(PassStateDto state)
        {
            Clear();
            if (state.Nodes == null) return;

            foreach (var node in state.Nodes.OrderBy(n => n.Index))
            {
                SpawnCard(node, golden: true, state);
                SpawnCard(node, golden: false, state);
                SpawnMarker(node, state);
            }
        }

        private void SpawnCard(PassNodeDto node, bool golden, PassStateDto state)
        {
            var row = golden ? goldenRow : freeRow;
            if (row == null) return;

            var lines = Visible(golden ? node.Golden : node.Free);
            var cardState = StateOf(node, golden, state);
            var prefab = PickVariant(node, lines, golden, cardState, node.Index <= state.MaxNode);
            if (prefab == null) return;

            var card = Instantiate(prefab, row);
            card.name = $"{(golden ? "Golden" : "Free")}_Day{node.Index}";
            // The headline is the SERVER's — authored per card, or derived there. The client never invents it, so a
            // day can advertise "Mystery" while paying three things behind it.
            var text = golden ? node.GoldenText : node.FreeText;
            card.Bind(node.Index, text, null, null, cardState, state.AdsPerUnlock);
            card.Clicked += OnCardClicked;

            LoadArt(card, lines);
            (golden ? _goldenCards : _freeCards).Add(card);
        }

        private void SpawnMarker(PassNodeDto node, PassStateDto state)
        {
            if (markerPrefab == null || markerRow == null) return;

            var marker = Instantiate(markerPrefab, markerRow);
            marker.name = $"Day{node.Index}";
            marker.Bind(node.Index,
                node.Claimed ? PassDayMarkerState.Past
                : node.Index == state.MaxNode ? PassDayMarkerState.Current
                : node.Index < state.MaxNode ? PassDayMarkerState.Past
                : PassDayMarkerState.Future);
            _markers.Add(marker);
        }

        /// <summary>
        /// Which prefab a day uses: collectible art while the collectible is still uncollected, milestone art for a
        /// flagged day, otherwise by how many rewards it shows. Any unassigned variant falls back to the single-item
        /// one, so a half-wired screen still renders.
        /// </summary>
        /// <summary>
        /// Which prefab a day uses. The COLLECTIBLE variants are the "take it now" art — sparkles, no padlock, and
        /// (on the free one) the ad badge — so they're chosen by STATE, not by what the day pays: a day that can be
        /// collected right now, or bought back with ads, gets the collectible card. The moment it's taken, the day
        /// falls back to its plain variant with Collected on and tapping off; a day that hasn't arrived, or that only
        /// a subscription reaches, likewise uses the plain art.
        /// </summary>
        private PassCardView PickVariant(PassNodeDto node, List<RewardGrant> lines, bool golden,
            PassCardState cardState, bool arrived)
        {
            bool two = lines.Count >= 2;

            // Every day that has ARRIVED uses the collectible art — free or golden, takeable or locked. The lock and
            // ad badges live inside those prefabs, so a locked day still reads as locked; only a day already taken
            // (plain + Collected) or one that hasn't arrived falls back to the plain variant.
            bool collectNow = arrived && cardState != PassCardState.Collected;

            if (collectNow)
            {
                if (golden)
                {
                    var collect = two ? (goldenCollectibleTwoItems ?? goldenCollectible)
                                      : (goldenCollectible ?? goldenCollectibleTwoItems);
                    if (collect != null) return collect;
                }
                else if (freeCollectible != null) return freeCollectible;
            }

            // A single-reward day can have art of its own — the Kash frame vs the chip frame.
            if (!two && lines.Count == 1)
            {
                var byReward = Match(golden ? goldenByReward : freeByReward, lines[0].Id, node.IsMilestone);
                if (byReward != null) return byReward;
            }

            if (golden)
            {
                if (node.IsMilestone && goldenMilestone != null) return goldenMilestone;
                if (two && goldenTwoItems != null) return goldenTwoItems;
                return goldenOneItem;
            }
            return freeOneItem;
        }

        /// <summary>Best match for a reward id: a milestone-specific entry wins, then a general one.</summary>
        private static PassCardView Match(CardVariant[] variants, string rewardId, bool isMilestone)
        {
            if (variants == null || string.IsNullOrEmpty(rewardId)) return null;

            PassCardView general = null;
            foreach (var v in variants)
            {
                if (v == null || v.prefab == null || !string.Equals(v.rewardId, rewardId, StringComparison.OrdinalIgnoreCase)) continue;
                if (v.milestoneOnly) { if (isMilestone) return v.prefab; }
                else general = general ?? v.prefab;
            }
            return general;
        }

        /// <summary>
        /// The two rows of one day legitimately differ. Rewarded ads buy back the FREE reward of a missed day; the
        /// golden half of that day is still only reachable with a subscription, so the ad badge belongs on the free
        /// card and the padlock on the golden one. A day claimed without a subscription is likewise Collected on the
        /// free row while the golden row stays locked — which is precisely what the retro unlock later pays out.
        /// </summary>
        private PassCardState StateOf(PassNodeDto node, bool golden, PassStateDto state)
        {
            if (golden)
            {
                if (node.GoldenClaimed) return PassCardState.Collected;
                return state.IsGolden ? PassCardState.Default : PassCardState.Locked;
            }

            if (node.Claimed) return PassCardState.Collected;
            if (node.AdUnlockable) return PassCardState.AdUnlockable;
            if (node.GoldenLocked) return PassCardState.Locked;
            return PassCardState.Default;
        }

        private void OnCardClicked(PassCardView card)
        {
            switch (card.State)
            {
                case PassCardState.Locked:
                    SubscribeRequested?.Invoke();          // a locked day IS the subscribe pitch
                    break;
                case PassCardState.AdUnlockable:
                    ClaimRequested?.Invoke(card.Day, true);
                    break;
                case PassCardState.Collected:
                    break;                                  // finished
                default:
                    var node = _state?.Nodes?.FirstOrDefault(n => n.Index == card.Day);
                    if (node != null && node.ClaimableNow) ClaimRequested?.Invoke(card.Day, false);
                    break;
            }
        }

        // ---------------- rewards ----------------

        /// <summary>The lines worth drawing on a card. XP has no art in this design and would eat an item slot.</summary>
        private static List<RewardGrant> Visible(List<RewardGrant> lines)
            => (lines ?? new List<RewardGrant>()).Where(l => l != null && l.Kind != RewardKind.Xp).ToList();

        /// <summary>
        /// Server art overrides the prefab's, per reward. Downloads are async and the card may be gone by the time
        /// one lands (a rebuild, a closed panel), so every callback re-checks the card is still alive and still on
        /// the same day.
        /// </summary>
        private void LoadArt(PassCardView card, List<RewardGrant> lines)
        {
            for (int i = 0; i < lines.Count && i < 2; i++)
            {
                var images = lines[i].Images;
                if (images == null || images.Count == 0) continue;

                int slot = i;
                int day = card.Day;
                RemoteImage.Load(images[0], sprite =>
                {
                    if (sprite == null || card == null || card.Day != day) return;
                    card.SetIcon(slot, sprite);
                });
            }
        }

        // ---------------- plumbing ----------------

        private void ScrollToToday(PassStateDto state)
        {
            if (scrollRect == null || state.Days <= 1) return;

            // Re-measure, then SIZE the rows to what they actually need. A layout group positions children happily
            // outside its own rect, so without this a row keeps its authored width, the ScrollRect thinks the content
            // is short, and the progress bar measures itself against a stub.
            ForceRebuild(goldenRow);
            ForceRebuild(freeRow);
            ForceRebuild(markerRow);

            float ladder = Mathf.Max(FitRowWidth(goldenRow), Mathf.Max(FitRowWidth(freeRow), FitRowWidth(markerRow)));
            MatchProgressBarToLadder(ladder);
            FitContentWidth(ladder);

            ForceRebuild(scrollRect.content);
            Canvas.ForceUpdateCanvases();

            // Land on today rather than day 1: the thing the player came to tap should be on screen.
            scrollRect.horizontalNormalizedPosition = Mathf.Clamp01((state.MaxNode - 1f) / (state.Days - 1f));
            CullOffscreenFx();
        }

        private static void ForceRebuild(RectTransform target)
        {
            if (target != null) LayoutRebuilder.ForceRebuildLayoutImmediate(target);
        }

        /// <summary>
        /// Give a row the width its layout group actually needs, and return it.
        ///
        /// A HorizontalLayoutGroup positions children wherever they fall — happily past its own edge — so a row that
        /// was authored 1822 px wide stays 1822 px with 31 cards spilling out of it. Anything that measures the row
        /// (the ScrollRect's content, the progress bar) then reads a stub. A ContentSizeFitter would do this, but not
        /// every row has one, so do it here rather than depend on the prefab being wired a particular way.
        /// </summary>
        private static float FitRowWidth(RectTransform row)
        {
            if (row == null) return 0f;

            float preferred = LayoutUtility.GetPreferredWidth(row);
            if (preferred <= 1f) return row.rect.width;

            // A row that already has a fitter sizes itself; writing over it would just be undone.
            if (row.GetComponent<ContentSizeFitter>() == null && Mathf.Abs(row.rect.width - preferred) > 1f)
                row.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, preferred);

            return Mathf.Max(preferred, row.rect.width);
        }

        /// <summary>Size the ScrollRect's content to the ladder, so it can actually scroll the whole month.</summary>
        private void FitContentWidth(float ladder)
        {
            var content = scrollRect != null ? scrollRect.content : null;
            if (content == null || ladder <= 1f) return;
            if (content.GetComponent<ContentSizeFitter>() != null) return;   // it sizes itself
            if (Mathf.Abs(content.rect.width - ladder) < 1f) return;

            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, ladder);
        }

        /// <summary>
        /// Stretch the progress bar to the width of the day markers.
        ///
        /// The bar's fill is a fraction of the BAR, while the markers are laid out to fit however many days the cycle
        /// has — so a bar authored at a fixed width (for a mockup of ~19 days) shows "half full" somewhere around day
        /// 10 of 31. Matching the two every build keeps the fill under the right marker for any month length, card
        /// width or spacing, with nothing to re-author by hand.
        /// </summary>
        private void MatchProgressBarToLadder(float target)
        {
            if (!matchProgressBarToMarkers) return;

            var slider = ResolveProgressSlider();
            var bar = slider != null ? slider.transform as RectTransform : null;
            if (bar == null || bar == markerRow) return;

            if (target <= 1f || Mathf.Abs(bar.rect.width - target) < 1f) return;

            // Keep the LEFT edge where the artist put it; only the length changes.
            float left = bar.anchoredPosition.x - bar.rect.width * bar.pivot.x;
            bar.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, target);
            bar.anchoredPosition = new Vector2(left + target * bar.pivot.x, bar.anchoredPosition.y);
        }

        /// <summary>
        /// Empty the three rows. Everything in them is ours to remove — including anything left over from the mockup,
        /// which would otherwise offset every index and size the scroll content to the wrong width.
        ///
        /// Children are DETACHED before Destroy: Unity defers the actual destruction to end of frame, so a layout
        /// group would still count them while the new ladder is being built.
        /// </summary>
        private void Clear()
        {
            _goldenCards.Clear();
            _freeCards.Clear();
            _markers.Clear();

            ClearChildren(goldenRow);
            ClearChildren(freeRow);
            ClearChildren(markerRow);

            _contentFx = null;   // the spawned FX are gone; re-scan on the next cull
        }

        private static void ClearChildren(Transform row)
        {
            if (row == null) return;
            for (int i = row.childCount - 1; i >= 0; i--)
            {
                var child = row.GetChild(i);
                child.SetParent(null, false);
                Destroy(child.gameObject);
            }
        }

        private static string FormatSpan(TimeSpan span, bool longForm)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (longForm) return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
            return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";
        }
    }
}
