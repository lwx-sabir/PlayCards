using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Blackjack;
using PlayCard.App;
using PlayCard.Game.Betting;   // ChipView.Format — the same "1k / 10k" shorthand the chips use
using PlayCard.Game.Net;
using PlayCard.Game.Wallet;
using PlayCard.Home;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Lobby = a live carousel of open tables for the game chosen on Home (<see cref="GameSession.SelectedGame"/>).
    /// Fetches GET /api/lobby/blackjack?mode= and spawns a <see cref="LobbyTableCard"/> per table under the
    /// carousel root, then tells the <see cref="CarouselController"/> to rebuild. The shared HUD <b>Join</b>
    /// seats the player at the centred table; the <c>&lt; &gt;</c> arrows are wired to the carousel's Prev/Next.
    /// (v1: blackjack only; mode tabs + stakes-tier filter come later.)
    /// </summary>
    public sealed class LobbyController : MonoBehaviour
    {
        [Header("Carousel")]
        [Tooltip("CarouselController on the ring root (also the parent the cards spawn under).")]
        [SerializeField] private CarouselController carousel;
        [SerializeField] private Transform cardParent;       // usually the carousel root's transform
        [SerializeField] private LobbyTableCard cardPrefab;

        [Header("Centred-table HUD")]
        [SerializeField] private Button joinButton;          // joins the centred table
        [SerializeField] private TMP_Text betRangeText;      // centre stake text, mirrors the centred card
        [SerializeField] private TMP_Text membersText;       // "2/5" for the centred table

        [Header("Chrome")]
        [SerializeField] private Button backButton;
        [SerializeField] private Button refreshButton;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text statusText;
        [Tooltip("HUD status/loading-spinner container — shown only while there's a message, hidden otherwise. " +
                 "Falls back to the statusText's own GameObject if left empty.")]
        [SerializeField] private GameObject statusRoot;
        [SerializeField] private TMP_Text balanceText;

        [Header("Filter")]
        [SerializeField] private BlackjackMode mode = BlackjackMode.Classic;
        [Tooltip("The ◀ 1k/10k ▶ stake strip. Optional — without it the lobby just shows the first tier.")]
        [SerializeField] private BetTierSlider tierSlider;

        [Header("Tier change transition")]
        [Tooltip("How far the ring travels when swapping tiers, in the card parent's local units. Set it past the " +
                 "edge of the view so the tables leave the screen rather than shrinking away in the middle.")]
        [SerializeField] private float slideDistance = 14f;
        [Tooltip("Seconds for the swap. Both sets move over this same span — the old one leaving and the new one " +
                 "arriving are one motion, not two.")]
        [SerializeField] private float slideSeconds = 0.28f;

        private readonly List<LobbyTableCard> _cards = new();
        private LobbyTableCard _current;
        private bool _loading;
        private int _renderedTier = -1;   // the tier the cards on screen actually describe

        // ---- Stake tier ----------------------------------------------------------------------------------------
        // A tier is a stake bracket holding MANY tables, so the carousel shows one tier at a time and the ◀ ▶ arrows
        // step between tiers; dragging the carousel moves between the tables inside the current tier.
        // Static so the chosen tier survives leaving and re-entering the lobby.
        private static List<BetTierData> _tiers;
        private static int _tierIndex;

        /// <summary>The selected tier. Note a refresh pins its own copy for the duration of the pass — the player
        /// can move the strip again while it is still fetching, and one pass must describe one tier throughout.</summary>
        private BetTierData CurrentTier =>
            _tiers != null && _tiers.Count > 0 ? _tiers[Mathf.Clamp(_tierIndex, 0, _tiers.Count - 1)] : null;

        /// <summary>Next stake tier — wire the ▶ button's OnClick to this (replacing CarouselController.Next).</summary>
        public void NextTier() => StepTier(+1);

        /// <summary>Previous stake tier — wire the ◀ button's OnClick to this (replacing CarouselController.Prev).</summary>
        public void PrevTier() => StepTier(-1);

        private void StepTier(int delta)
        {
            if (_tiers == null || _tiers.Count == 0) return;

            // Clamped, not wrapped: the strip has visible ends and greys its arrows there, so wrapping would
            // contradict what the player is looking at.
            int next = Mathf.Clamp(_tierIndex + delta, 0, _tiers.Count - 1);
            if (next == _tierIndex) return;

            if (tierSlider) tierSlider.MoveTo(next);   // the slider reports back and the refresh happens there
            OnTierPicked(next);
        }

        /// <summary>The player landed on a tier — by swiping the strip or tapping an arrow. Reload that bracket.</summary>
        private void OnTierPicked(int index)
        {
            if (_tiers == null || _tiers.Count == 0) return;
            index = Mathf.Clamp(index, 0, _tiers.Count - 1);
            if (index == _tierIndex && _cards.Count > 0) return;

            // Travel direction, so the tables leave and arrive on the sides that match the strip's movement.
            int direction = index > _tierIndex ? 1 : index < _tierIndex ? -1 : 0;
            _tierIndex = index;

            // A pass already running would drop this one on the floor: a refresh holds _loading for the fetch AND
            // the slide, which is longer than the strip takes to settle, so swiping at any speed used to leave the
            // tables showing a tier the player had already moved past. _tierIndex is the wanted state — whoever is
            // mid-flight notices the difference when it finishes and catches up.
            if (_loading) return;
            _ = RefreshAsync(direction);
        }

        private void Awake()
        {
            if (backButton) backButton.onClick.AddListener(SceneNavigator.GoToHome);
            if (refreshButton) refreshButton.onClick.AddListener(() => _ = RefreshAsync());
            if (joinButton) joinButton.onClick.AddListener(JoinCurrent);
        }

        private async void OnEnable()
        {
            if (carousel) carousel.OnSelectionChanged += OnSelected;
            if (titleText) titleText.text = (GameSession.SelectedGame ?? "blackjack").ToUpperInvariant();

            if (WalletManager.Instance != null)
            {
                WalletManager.Instance.OnBalancesChanged += ShowBalance;
                await WalletManager.Instance.RefreshAsync();
            }
            await EnsureTiersAsync();
            await RefreshAsync();
        }

        /// <summary>
        /// Fetch the stake tiers once per session. If the server doesn't offer them (older build), we simply leave
        /// the list null and the lobby behaves exactly as before — every table, unfiltered — rather than showing
        /// nothing.
        /// </summary>
        private async Task EnsureTiersAsync()
        {
            if (_tiers == null || _tiers.Count == 0)
            {
                var res = await BlackjackRestClient.Instance.GetTiersAsync();
                if (res.Ok && res.Value != null && res.Value.Count > 0) _tiers = res.Value;
            }
            if (_tiers == null || _tiers.Count == 0) return;

            _tierIndex = Mathf.Clamp(_tierIndex, 0, _tiers.Count - 1);
            if (tierSlider)
            {
                var labels = new List<string>(_tiers.Count);
                foreach (var t in _tiers) labels.Add($"{ChipView.Format((long)t.MinBet)}/{ChipView.Format((long)t.MaxBet)}");
                tierSlider.SetTiers(labels);
                tierSlider.MoveTo(_tierIndex, animate: false);   // returning to the lobby shouldn't replay the slide
                tierSlider.OnTierSelected -= OnTierPicked;
                tierSlider.OnTierSelected += OnTierPicked;
            }
        }

        private void OnDisable()
        {
            if (carousel) carousel.OnSelectionChanged -= OnSelected;
            if (tierSlider) tierSlider.OnTierSelected -= OnTierPicked;
            if (WalletManager.Instance != null) WalletManager.Instance.OnBalancesChanged -= ShowBalance;
        }

        /// <summary>Switch rule variant — hook the mode tabs (Classic/Hi-Lo/…) here. Re-fetches.</summary>
        public void SetMode(int modeIndex)
        {
            mode = (BlackjackMode)modeIndex;
            _ = RefreshAsync();
        }

        private async Task RefreshAsync(int direction = 0)
        {
            if (_loading) return;
            _loading = true;

            // Only announce loading when there is nothing to look at. Switching tiers keeps the previous tables on
            // screen until the new ones are ready, so a status line there would just flash.
            if (_cards.Count == 0) SetStatus("Loading tables…");

            // Pin the tier for THIS pass. _tierIndex can move again while we are awaiting the fetch or animating,
            // and everything below — the query, the cards, the HUD — has to describe one consistent tier.
            int target = Mathf.Clamp(_tierIndex, 0, Mathf.Max(0, (_tiers?.Count ?? 1) - 1));

            // Restrict to the selected stake tier. Null tier = no tiers from the server, so fall back to the whole
            // lobby exactly as before.
            var tier = _tiers != null && _tiers.Count > 0 ? _tiers[target] : null;
            var res = await BlackjackRestClient.Instance.GetLobbyAsync(mode, tier?.MinBet, tier?.MaxBet);
            if (!res.Ok)
            {
                SetStatus($"Couldn't load tables: {res.Error}");
                _loading = false;
                return;
            }

            // The data is here and the OLD tables are still on screen. Lift them out of the carousel into a holder
            // that will carry them off, so the outgoing and incoming sets can move AT THE SAME TIME. Sliding one
            // out and then the other in reads as "the screen emptied, then something arrived" — the two have to
            // overlap for it to look like one set replacing another.
            Transform outgoing = direction != 0 ? DetachCardsForExit() : null;

            ClearCards();
            var tables = res.Value ?? new List<BlackjackTableSummary>();
            foreach (var summary in tables)
            {
                if (!cardPrefab || !cardParent) break;
                var card = Instantiate(cardPrefab, cardParent);
                card.Bind(summary);
                card.OnStatus += SetStatus;   // card's join/loading feedback → HUD status
                _cards.Add(card);
            }

            if (carousel)
            {
                carousel.Rebuild();          // re-scan + centre → raises OnSelectionChanged for the centred card
                // ...and drive the HUD from the result directly too, so the readout does not depend on the
                // carousel's event ordering during a rebuild.
                OnSelected(carousel.Current);
            }

            if (direction != 0)
            {
                // Both sets move together: the old one leaves as the new one arrives.
                SetRingOffset(direction * slideDistance);
                var exit = SlideAndDestroyAsync(outgoing, -direction * slideDistance, slideSeconds);
                var enter = SlideRingAsync(direction * slideDistance, 0f, slideSeconds);
                await Task.WhenAll(exit, enter);
            }
            else if (cardParent) SetRingOffset(0f);
            // An empty tier is a server-side balancing hiccup, not "no tables anywhere" — say which one is empty.
            SetStatus(tables.Count == 0
                ? (tier != null ? $"No tables at {tier.MinBet:0}–{tier.MaxBet:0} yet." : "No tables yet.")
                : string.Empty);

            _renderedTier = target;
            _loading = false;

            // The player kept swiping while this was running — go again for wherever they actually ended up. This
            // is what stops a fast swipe from leaving the lobby on a tier nobody selected.
            if (_tiers != null && _tiers.Count > 0 && _tierIndex != _renderedTier)
                _ = RefreshAsync(_tierIndex > _renderedTier ? 1 : -1);
        }

        // ---- tier change transition ----------------------------------------------------------------------------
        // The whole ring is carried sideways rather than each card animated: the carousel owns where its cards sit,
        // so moving their shared parent is the one way to slide them without fighting it for control.

        private Vector3 _ringHome;
        private bool _ringHomeCaptured;

        /// <summary>
        /// Move the cards currently on screen out of the carousel and into a plain holder alongside it, so they can
        /// be animated away while the carousel builds and animates the next tier. They are no longer the carousel's
        /// children, so it stops repositioning them and they simply travel with the holder.
        /// </summary>
        private Transform DetachCardsForExit()
        {
            if (_cards.Count == 0 || !cardParent) return null;

            var holder = new GameObject("OutgoingTables").transform;
            // If the ring sits at the scene root there is no parent to sit beside — the holder becomes a root
            // object instead. Either way it must NOT end up inside the carousel, or the cards leaving would be
            // re-collected as part of the incoming tier.
            holder.SetParent(cardParent.parent, worldPositionStays: false);
            holder.localPosition = cardParent.localPosition;
            holder.localRotation = cardParent.localRotation;
            holder.localScale = cardParent.localScale;

            foreach (var c in _cards)
            {
                if (!c) continue;
                c.OnStatus -= SetStatus;                              // it is leaving; its status must not reach the HUD
                c.transform.SetParent(holder, worldPositionStays: true);
            }
            _cards.Clear();      // cleared, NOT destroyed — the holder owns them now
            _current = null;
            return holder;
        }

        /// <summary>Carry a detached set off screen and dispose of it.</summary>
        private async Task SlideAndDestroyAsync(Transform holder, float toX, float seconds)
        {
            if (!holder) return;

            Vector3 home = holder.localPosition;
            float t = 0f;
            while (t < 1f && holder)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(seconds, 0.0001f);
                float e = 1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f);
                holder.localPosition = home + new Vector3(Mathf.LerpUnclamped(0f, toX, e), 0f, 0f);
                await Task.Yield();
            }
            if (holder) Destroy(holder.gameObject);
        }

        private void SetRingOffset(float x)
        {
            if (!cardParent) return;
            if (!_ringHomeCaptured) { _ringHome = cardParent.localPosition; _ringHomeCaptured = true; }
            cardParent.localPosition = _ringHome + new Vector3(x, 0f, 0f);
        }

        private async Task SlideRingAsync(float fromX, float toX, float seconds)
        {
            if (!cardParent) return;
            if (seconds <= 0f) { SetRingOffset(toX); return; }

            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / seconds;
                float e = 1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f);   // ease-out; the strip already did the bounce
                SetRingOffset(Mathf.LerpUnclamped(fromX, toX, e));
                await Task.Yield();
                if (this == null || !isActiveAndEnabled) return;        // left the lobby mid-slide
            }
            SetRingOffset(toX);
        }

        // Centred table changed (drag / arrows / rebuild): mirror its stakes + seats to the HUD.
        private void OnSelected(ICarouselItem item)
        {
            _current = item as LobbyTableCard;
            if (betRangeText) betRangeText.text = _current != null ? _current.BetLabel : string.Empty;
            if (membersText) membersText.text = _current != null ? _current.PlayersLabel : string.Empty;
            if (joinButton) joinButton.interactable = _current != null && _current.CanJoin;
        }

        private void JoinCurrent() => _current?.Join();

        private void ClearCards()
        {
            foreach (var c in _cards)
            {
                if (!c) continue;
                c.OnStatus -= SetStatus;

                // Unparent BEFORE destroying. Destroy() is deferred to the end of the frame, so a card that is
                // merely "destroyed" is still a child of the carousel for the rest of this one — and the carousel
                // rebuilds by walking its children. Leave them in place and it re-collects the dying cards
                // alongside the new ones, centres on a dead one, and reports it as the selection: the readout then
                // describes the tier you just left, permanently one behind.
                c.transform.SetParent(null, worldPositionStays: false);
                Destroy(c.gameObject);
            }
            _cards.Clear();
            _current = null;
        }

        private void ShowBalance(WalletBalances b)
        {
            if (balanceText && b != null) balanceText.text = $"{b.Chips:0}";
        }

        // Show the HUD status/spinner only while there's a message; hide it when cleared.
        private void SetStatus(string s)
        {
            bool has = !string.IsNullOrEmpty(s);
            if (statusText && has) statusText.text = s;
            if (statusRoot) statusRoot.SetActive(has);
            else if (statusText) statusText.gameObject.SetActive(has);
        }
    }
}
