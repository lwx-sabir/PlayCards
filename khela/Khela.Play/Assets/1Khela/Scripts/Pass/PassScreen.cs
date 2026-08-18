using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
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

        [Header("Opening scroll — day 1, then travel to today")]
        [Tooltip("Open on day 1 and ride the ladder to today, instead of starting there. It shows the player what " +
                 "they've already collected and how far the month has come — the trip IS the reward summary.")]
        [SerializeField] private bool scrollFromDayOne = true;
        [Tooltip("Beat before the ride starts, so the panel's own open tween lands first.")]
        [SerializeField] private float scrollDelay = 0.35f;
        [Tooltip("How long the ride takes. Long enough to read the days going past; short enough not to be a wait.")]
        [SerializeField] private float scrollSeconds = 1.1f;
        [Tooltip("Ease in and out so it pulls away and settles, rather than starting and stopping dead.")]
        [SerializeField] private Ease scrollEase = Ease.InOutCubic;

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
        [Tooltip("Reserve the golden COLLECTIBLE art for TODAY only. Earlier days that are still open fall back to " +
                 "their plain variant (by reward, milestone, one/two item) even though they can still be taken.\n\n" +
                 "The collectible card is a call to action — glow, sparkle, no padlock. With catch-up on, a whole row " +
                 "of open past days all shout equally and none of them reads as urgent. This keeps the shout for the " +
                 "day that actually renews. Golden row only; the free row is unaffected.")]
        [SerializeField] private bool goldenCollectibleTodayOnly;

        [Header("Free card variants")]
        [SerializeField] private PassCardView freeOneItem;
        [SerializeField] private PassCardView freeCollectible;

        [Header("Day marker")]
        [Tooltip("Optional numbered marker prefab; leave empty until it exists.")]
        [SerializeField] private PassDayMarkerView markerPrefab;

        [Header("Header")]
        [SerializeField] private TMP_Text titleText;
        [Tooltip("A SECOND label showing the same title — a banner as well as the header, say. Optional; both are " +
                 "written from the server's title so they can't drift apart.")]
        [SerializeField] private TMP_Text titleTextAlt;
        [Tooltip("\"Ends in 12d 4h\" — counts down to the end of the player's own month.")]
        [SerializeField] private TMP_Text endsInText;
        [Tooltip("Optional \"next day in HH:MM:SS\" label.")]
        [SerializeField] private TMP_Text nextDayText;

        [Tooltip("Which day the player is on — the small badge beside the header bar. Formatted with Day Format.")]
        [SerializeField] private TMP_Text dayText;
        [Tooltip("{0} = current day, {1} = days in the cycle. \"{0}\" → \"3\"; \"{0} / {1}\" → \"3 / 31\".")]
        [SerializeField] private string dayFormat = "{0}";

        [Tooltip("The SMALL header bar — the one beside the day badge. Separate from the ladder bar above, which is " +
                 "stretched to the day markers; this one keeps its authored size. Assign the Slider, or just its " +
                 "fill Image and the Slider that owns it is found.")]
        [SerializeField] private Slider headerProgressSlider;
        [SerializeField] private Image headerProgressFill;
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

        /// <summary>The player tapped Close. Subscribed by <see cref="PassPanel"/> so the panel can play its way out;
        /// with nobody listening the screen just hides itself, as before.</summary>
        public event Action CloseRequested;

        /// <summary>
        /// A day card was tapped: its state, and whether the tap is actually a COLLECT (as opposed to a locked day
        /// opening the subscribe sheet, or a finished one). Raised for every tap so audio can speak to all of them,
        /// but the flag is what separates "the collect just started" from "that did nothing".
        /// </summary>
        public event Action<PassCardState, bool> CardTapped;

        /// <summary>The card the player last tried to claim — where the collect juice should burst FROM. Captured on
        /// tap because the claim's rewards arrive after a round trip, by which time the ladder may have rebuilt.</summary>
        public RectTransform LastClaimSource { get; private set; }

        private readonly List<PassCardView> _goldenCards = new List<PassCardView>();
        private readonly List<PassCardView> _freeCards = new List<PassCardView>();
        private readonly List<PassDayMarkerView> _markers = new List<PassDayMarkerView>();

        // Which prefab each spawned card came from, index-aligned with the rows. A refresh only has to respawn a card
        // when its variant changes, and this is how we know.
        private readonly List<PassCardView> _goldenPrefabs = new List<PassCardView>();
        private readonly List<PassCardView> _freePrefabs = new List<PassCardView>();

        private PassStateDto _state;
        private Coroutine _ticker;
        private string _builtCycle;      // the cycle the current ladder was built for
        private bool _rode;              // already rode this opening — a claim must never yank the view back to today

        private void Awake()
        {
            if (activateButton != null) activateButton.onClick.AddListener(() => SubscribeRequested?.Invoke());
            // Hiding outright is the FALLBACK. When a PassPanel is listening it wants the close tween to play first —
            // disabling the object on the tap kills that tween before its first frame.
            if (closeButton != null)
                closeButton.onClick.AddListener(() => { if (CloseRequested != null) CloseRequested.Invoke(); else Hide(); });
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

        // Re-arm the opening ride. Closing the panel only deactivates it — the ladder survives — so without this the
        // trip from day 1 would play exactly once per session.
        private void OnEnable() => _rode = false;

        private void OnDisable()
        {
            if (_ticker != null) { StopCoroutine(_ticker); _ticker = null; }
            _ride?.Kill();
            _ride = null;
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
                SetTitle(string.Empty);
                if (endsInText != null) endsInText.text = string.Empty;
                if (dayText != null) dayText.text = string.Empty;
                if (activateButton != null) activateButton.gameObject.SetActive(false);
                return;
            }

            BuildHeader(state);
            bool rebuilt = BuildLadder(state);
            // Re-measuring the ladder and flushing the canvas is only needed when the ladder actually changed shape.
            // After a claim nothing moved, so skip it — that pass is the other half of the frame spike.
            if (rebuilt) MeasureLadder(state);
            else CullOffscreenFx();

            // The opening ride, ONCE per opening — not once per build. Reopening the panel doesn't rebuild the ladder
            // (the cards are still there, so it refreshes in place), yet the player still expects the trip.
            if (!_rode && scrollRect != null && state.Days > 1)
            {
                _rode = true;
                RideToToday(Mathf.Clamp01((state.MaxNode - 1f) / (state.Days - 1f)));
            }

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

        private void SetTitle(string title)
        {
            if (titleText != null) titleText.text = title;
            if (titleTextAlt != null) titleTextAlt.text = title;
        }

        private void BuildHeader(PassStateDto state)
        {
            // The server's title, both places. The fallback is only for a config with no title at all — never a name
            // to hardcode in the art, since an admin can retitle the pass at any time.
            SetTitle(string.IsNullOrEmpty(state.Title) ? "Monthly Pass" : state.Title);

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

            // PassProgressBar owns the Slider-vs-Image problem (a Slider rewrites its fill's fillAmount from its own
            // value, so writing the Image is silently undone). Shared, so every screen showing pass progress gets it
            // right rather than each rediscovering it.
            float progress = state.Days > 0 ? (float)state.DayIndex / state.Days : 0f;
            Bar().Set(progress);
            HeaderBar().Set(progress);   // the same fraction, so the two bars can never tell different stories

            if (dayText != null) dayText.text = state.Days > 0 ? FormatDay(state.DayIndex, state.Days) : string.Empty;
        }

        /// <summary>A bad format string in the inspector must not throw on every refresh.</summary>
        private string FormatDay(int day, int days)
        {
            if (string.IsNullOrEmpty(dayFormat)) return day.ToString();
            try { return string.Format(dayFormat, day, days); }
            catch (FormatException) { return day.ToString(); }
        }

        private PassProgressBar Bar() => _bar ?? (_bar = new PassProgressBar(progressSlider, progressFill));

        private PassProgressBar HeaderBar()
            => _headerBar ?? (_headerBar = new PassProgressBar(headerProgressSlider, headerProgressFill));

        private PassProgressBar _bar;
        private PassProgressBar _headerBar;

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

        /// <summary>Builds (or refreshes) the ladder. Returns true when it was rebuilt from scratch.</summary>
        private bool BuildLadder(PassStateDto state)
        {
            if (state.Nodes == null) { Clear(); return true; }

            // Rebuilding all 31 days destroys and re-instantiates ~90 objects in one frame. That hitch is visible on
            // its own, and it lands exactly when the collect juice is starting — one long frame swallows the burst.
            // A refresh that only changed a day or two updates in place instead, and touches nothing else.
            if (CanUpdateInPlace(state)) { UpdateLadder(state); return false; }

            Clear();
            foreach (var node in state.Nodes.OrderBy(n => n.Index))
            {
                SpawnCard(node, golden: true, state);
                SpawnCard(node, golden: false, state);
                SpawnMarker(node, state);
            }
            _builtCycle = state.CycleKey;
            return true;
        }

        private bool CanUpdateInPlace(PassStateDto state)
            => _goldenCards.Count == state.Nodes.Count
            && _freeCards.Count == state.Nodes.Count
            && string.Equals(_builtCycle, state.CycleKey, StringComparison.Ordinal);

        /// <summary>
        /// Re-bind the existing cards. A card is only replaced when its VARIANT has to change — a day that was
        /// collectible art and is now collected — so a claim costs two swaps, not ninety.
        /// </summary>
        private void UpdateLadder(PassStateDto state)
        {
            var nodes = state.Nodes.OrderBy(n => n.Index).ToList();
            for (int i = 0; i < nodes.Count; i++)
            {
                RebindCard(_goldenCards, _goldenPrefabs, i, nodes[i], golden: true, state, goldenRow);
                RebindCard(_freeCards, _freePrefabs, i, nodes[i], golden: false, state, freeRow);

                if (i < _markers.Count && _markers[i] != null)
                    _markers[i].Bind(nodes[i].Index, MarkerStateOf(nodes[i], state));
            }
            CullOffscreenFx();
        }

        private void RebindCard(List<PassCardView> cards, List<PassCardView> prefabs, int index, PassNodeDto node,
            bool golden, PassStateDto state, RectTransform row)
        {
            if (index >= cards.Count) return;

            var lines = Visible(golden ? node.Golden : node.Free);
            var cardState = StateOf(node, golden, state);
            var wanted = PickVariant(node, lines, golden, cardState, node.Index <= state.MaxNode, node.Index == state.MaxNode);
            var text = golden ? node.GoldenText : node.FreeText;

            var current = cards[index];
            bool sameVariant = current != null && index < prefabs.Count && prefabs[index] == wanted;

            if (sameVariant)
            {
                current.Bind(node.Index, text, null, null, cardState, state.AdsPerUnlock);
                LoadArt(current, lines);
                return;
            }

            if (wanted == null || row == null) return;

            // Variant changed: swap just this one, keeping its place in the row so the columns stay aligned. The old
            // card is DETACHED before Destroy — Unity defers the destruction to end of frame, and a layout group would
            // otherwise count both for a frame and visibly shove the whole row sideways.
            int sibling = current != null ? current.transform.GetSiblingIndex() : index;
            if (current != null)
            {
                current.Clicked -= OnCardClicked;
                current.transform.SetParent(null, false);
                Destroy(current.gameObject);
            }

            var card = Instantiate(wanted, row);
            card.name = $"{(golden ? "Golden" : "Free")}_Day{node.Index}";
            card.transform.SetSiblingIndex(sibling);
            card.Bind(node.Index, text, null, null, cardState, state.AdsPerUnlock);
            card.Clicked += OnCardClicked;
            LoadArt(card, lines);

            // Same stale-mesh hazard as a full build (see RefreshTextMeshes), but this path doesn't re-measure the
            // ladder, so there is nothing to hang the deferred pass off — refresh this one card's labels directly.
            var labels = card.GetComponentsInChildren<TMP_Text>(true);
            for (int j = 0; j < labels.Length; j++)
            {
                if (labels[j] == null) continue;
                IsolateFontMaterial(labels[j]);   // a card swapped in later needs the same isolation
                labels[j].ForceMeshUpdate();
            }

            cards[index] = card;
            if (index < prefabs.Count) prefabs[index] = wanted;
        }

        private static PassDayMarkerState MarkerStateOf(PassNodeDto node, PassStateDto state)
            => node.Claimed ? PassDayMarkerState.Past
             : node.Index == state.MaxNode ? PassDayMarkerState.Current
             : node.Index < state.MaxNode ? PassDayMarkerState.Past
             : PassDayMarkerState.Future;

        private void SpawnCard(PassNodeDto node, bool golden, PassStateDto state)
        {
            var row = golden ? goldenRow : freeRow;
            if (row == null) return;

            var lines = Visible(golden ? node.Golden : node.Free);
            var cardState = StateOf(node, golden, state);
            var prefab = PickVariant(node, lines, golden, cardState, node.Index <= state.MaxNode, node.Index == state.MaxNode);
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
            (golden ? _goldenPrefabs : _freePrefabs).Add(prefab);
        }

        private void SpawnMarker(PassNodeDto node, PassStateDto state)
        {
            if (markerPrefab == null || markerRow == null) return;

            var marker = Instantiate(markerPrefab, markerRow);
            marker.name = $"Day{node.Index}";
            marker.Bind(node.Index, MarkerStateOf(node, state));
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
            PassCardState cardState, bool arrived, bool isToday)
        {
            bool two = lines.Count >= 2;

            // Every day that has ARRIVED uses the collectible art — free or golden, takeable or locked. The lock and
            // ad badges live inside those prefabs, so a locked day still reads as locked; only a day already taken
            // (plain + Collected) or one that hasn't arrived falls back to the plain variant.
            bool collectNow = arrived && cardState != PassCardState.Collected;

            // …unless the golden row is reserving that art for today (see goldenCollectibleTodayOnly).
            if (collectNow && golden && goldenCollectibleTodayOnly && !isToday) collectNow = false;

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
            LastClaimSource = (RectTransform)card.transform;

            switch (card.State)
            {
                case PassCardState.Locked:
                    CardTapped?.Invoke(card.State, false);
                    card.PlayDenied();                      // it refuses AND pitches: the shake says no, the sheet says how
                    SubscribeRequested?.Invoke();
                    break;
                case PassCardState.AdUnlockable:
                    CardTapped?.Invoke(card.State, true);
                    ClaimRequested?.Invoke(card.Day, true);
                    break;
                case PassCardState.Collected:
                    CardTapped?.Invoke(card.State, false);
                    card.PlayAlreadyCollected();            // acknowledge the tap instead of doing nothing
                    break;
                default:
                    var node = _state?.Nodes?.FirstOrDefault(n => n.Index == card.Day);
                    bool collecting = node != null && node.ClaimableNow;
                    CardTapped?.Invoke(card.State, collecting);
                    if (collecting) { card.PlayClaimed(); ClaimRequested?.Invoke(card.Day, false); }
                    else card.PlayDenied();                 // a future day — the tap must not feel ignored
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

        /// <summary>Size the rows, the scroll content and the progress bar to the ladder that was just built.</summary>
        private void MeasureLadder(PassStateDto state)
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

            CullOffscreenFx();
            if (isActiveAndEnabled) StartCoroutine(RefreshTextMeshes());
        }

        /// <summary>
        /// Open on day 1 and travel to today.
        ///
        /// The ride is the point: it shows the month as a journey, past the days already collected, and arrives on the
        /// one the player came to tap. Jumping straight there skips the only moment the ladder is ever seen whole.
        ///
        /// It YIELDS to the player: the moment they drag, the ride stops where it is. That is checked by comparing the
        /// scroll position against what this last WROTE — anything else moved it, so it wasn't us. No input polling,
        /// and it catches a flick, a mouse wheel or a jump from any other script equally.
        /// </summary>
        private void RideToToday(float target)
        {
            _ride?.Kill();
            _ride = null;

            if (!scrollFromDayOne || scrollSeconds <= 0f || target <= 0f)
            {
                scrollRect.horizontalNormalizedPosition = target;
                return;
            }

            scrollRect.horizontalNormalizedPosition = 0f;
            _rideWrote = 0f;
            bool abandoned = false;

            _ride = DOVirtual.Float(0f, 1f, scrollSeconds, u =>
                {
                    if (abandoned || scrollRect == null) return;

                    // Someone else moved the view — the player. Their drag wins; stop steering.
                    if (Mathf.Abs(scrollRect.horizontalNormalizedPosition - _rideWrote) > 0.002f) { abandoned = true; return; }

                    // Writing the position raises onValueChanged, which already re-culls the cards' particles.
                    _rideWrote = Mathf.Lerp(0f, target, u);
                    scrollRect.horizontalNormalizedPosition = _rideWrote;
                })
                .SetEase(scrollEase)
                .SetDelay(Mathf.Max(0f, scrollDelay))
                .SetUpdate(true)          // unscaled, so it plays over a paused game
                .OnKill(() => _ride = null);
        }

        private Tween _ride;
        private float _rideWrote;

        /// <summary>
        /// Rebuild every spawned label's mesh once the layout has actually settled.
        ///
        /// A card is instantiated, <c>Bind</c> writes its text, and only THEN does the layout group size it — so TMP
        /// has already generated a mesh against the prefab's rect, and the forced rebuild + <c>ForceUpdateCanvases</c>
        /// that follows in the same frame can leave that stale mesh in place. The symptom is a card with its art, its
        /// lock and its state all correct and no text at all; in the editor something later dirties it and the text
        /// pops in, which is exactly why this looked like a data problem and only reproduced on device.
        ///
        /// Waiting a frame and forcing the mesh is the fix that holds regardless of what order Unity rebuilt things in.
        /// Cheap: it runs once per full ladder build, not per refresh.
        /// </summary>
        private IEnumerator RefreshTextMeshes()
        {
            yield return null;

            var content = scrollRect != null ? scrollRect.content : null;
            if (content == null) yield break;

            var labels = content.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i] == null) continue;
                IsolateFontMaterial(labels[i]);
                labels[i].ForceMeshUpdate();
            }

            ReportTextState(labels);
        }

        /// <summary>
        /// Say, once per build, whether the labels actually HAVE strings.
        ///
        /// Blank cards have two completely different causes that look identical on screen: the server sent no text, or
        /// the text is there and isn't drawing. On a device you can't inspect the hierarchy, so one line in the log
        /// separates them — and that is the difference between fixing the config and fixing the rendering.
        /// </summary>
        private void ReportTextState(TMP_Text[] labels)
        {
            int withText = 0;
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] != null && !string.IsNullOrEmpty(labels[i].text)) withText++;

            int nodesWithText = 0;
            if (_state?.Nodes != null)
                foreach (var n in _state.Nodes)
                    if (n != null && !string.IsNullOrWhiteSpace(n.FreeText)) nodesWithText++;

            Debug.Log($"[Pass] ladder built: {labels.Length} labels, {withText} carry a string; " +
                      $"{nodesWithText}/{_state?.Nodes?.Count ?? 0} nodes have FreeText from the server " +
                      $"(e.g. day 1 = '{_state?.Nodes?.FirstOrDefault()?.FreeText}'). " +
                      "Blank cards WITH strings here means it's rendering, not data.");

            DumpRenderState(labels);
        }

        /// <summary>
        /// Dump everything that can make a TMP label invisible while its string is correct, for ONE representative
        /// card label — and for a header label as the control, since that one demonstrably works on the same device.
        ///
        /// Comparing the two answers the question no amount of reasoning has: what is different about the label that
        /// doesn't draw. Font asset, material, shader, atlas texture, cull flag, alpha and mesh are the entire list of
        /// things that can be wrong once the text is known to be present.
        /// </summary>
        private void DumpRenderState(TMP_Text[] labels)
        {
            TMP_Text sample = null;
            for (int i = 0; i < labels.Length && sample == null; i++)
                if (labels[i] != null && !string.IsNullOrEmpty(labels[i].text)) sample = labels[i];

            Debug.Log("[Pass] CARD   " + Describe(sample));
            Debug.Log("[Pass] HEADER " + Describe(titleText));
        }

        private static string Describe(TMP_Text t)
        {
            if (t == null) return "(none)";

            var mat = t.fontSharedMaterial;
            var shader = mat != null ? mat.shader : null;
            var atlas = t.font != null ? t.font.atlasTexture : null;
            var cr = t.canvasRenderer;

            // The MASK is the other half of the question: which clipper, if any, is over this label.
            var rectMask = t.GetComponentInParent<RectMask2D>(true);
            var stencilMask = t.GetComponentInParent<Mask>(true);

            return $"'{t.name}' text='{t.text}' enabled={t.isActiveAndEnabled} " +
                   $"font='{(t.font != null ? t.font.name : "NULL")}' atlas={(atlas != null ? atlas.name + " " + atlas.width + "x" + atlas.height : "NULL")} " +
                   $"mat='{(mat != null ? mat.name : "NULL")}' shader='{(shader != null ? shader.name : "NULL")}' " +
                   $"shaderSupported={(shader != null && shader.isSupported)} " +
                   $"cull={(cr != null && cr.cull)} crAlpha={(cr != null ? cr.GetAlpha() : -1f):0.##} colorA={t.color.a:0.##} " +
                   $"chars={t.textInfo?.characterCount ?? -1} verts={(t.mesh != null ? t.mesh.vertexCount : -1)} " +
                   $"size={t.rectTransform.rect.width:0}x{t.rectTransform.rect.height:0} " +
                   $"rectMask={(rectMask != null ? rectMask.name : "none")} stencilMask={(stencilMask != null ? stencilMask.name : "none")} " +
                   // WHERE the label is versus WHERE the mask clips. Everything else is eliminated, so if the label's
                   // world rect sits inside the mask's and it still doesn't draw, clipping is exonerated too.
                   $"labelRect={Fmt(WorldRect(t.rectTransform))} " +
                   $"maskRect={(rectMask != null ? Fmt(WorldRect(rectMask.rectTransform)) : "n/a")} " +
                   // The material TMP actually RENDERS with. Masking makes it create an instance, and a difference
                   // between this and fontSharedMaterial is exactly what we're hunting.
                   $"renderMat='{(t.canvasRenderer.GetMaterial() != null ? t.canvasRenderer.GetMaterial().name : "NULL")}' " +
                   $"matCount={t.canvasRenderer.materialCount}";
        }

        private static string Fmt(Rect r) => $"x[{r.xMin:0}..{r.xMax:0}]y[{r.yMin:0}..{r.yMax:0}]";

        /// <summary>
        /// Give the labels INSIDE the scroll view their own copy of the font material.
        ///
        /// A RectMask2D writes its clip rect onto the material of everything it clips. These fonts render through
        /// TMP's BITMAP shader, where the label uses the shared material asset directly rather than a per-mask
        /// instance (the device log confirmed it: matCount=1, renderMat == the shared asset). The very same material
        /// is on the header, which no mask clips — so two clippers write one piece of state and the masked text loses
        /// it. That is why every card was blank on device while the header, on the identical material, drew fine.
        ///
        /// ONE copy per source material, shared by every label in the ladder: the isolation is between masked and
        /// unmasked text, not between individual labels, so batching is preserved. Doing it here rather than as a
        /// Material Preset asset keeps it working for any prefab that gets added later, with nothing to remember.
        /// </summary>
        private void IsolateFontMaterial(TMP_Text label)
        {
            var shared = label.fontSharedMaterial;
            if (shared == null) return;

            foreach (var mine in _maskedFontMaterials)
                if (mine.Value == shared) return;   // already one of ours

            if (!_maskedFontMaterials.TryGetValue(shared, out var copy) || copy == null)
            {
                copy = new Material(shared) { name = shared.name + " (Pass, masked)" };
                _maskedFontMaterials[shared] = copy;
            }
            label.fontSharedMaterial = copy;
        }

        private readonly Dictionary<Material, Material> _maskedFontMaterials = new Dictionary<Material, Material>();

        private void OnDestroy()
        {
            // Runtime-created materials are not collected with the object that referenced them.
            foreach (var copy in _maskedFontMaterials.Values)
                if (copy != null) Destroy(copy);
            _maskedFontMaterials.Clear();
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

            var bar = Bar().Rect;
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
            _goldenPrefabs.Clear();
            _freePrefabs.Clear();
            _builtCycle = null;

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
