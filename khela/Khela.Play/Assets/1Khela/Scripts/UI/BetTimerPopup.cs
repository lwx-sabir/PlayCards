using System;
using System.Collections;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// The "Place Your Bets" countdown. Slides up BETWEEN rounds for the length of the server's betting window and
    /// slides away when the round starts — the mirror image of <see cref="TurnPopup"/>, which owns the in-round
    /// decision clock. The window is authoritative: when it expires the server deals whatever bets are down, so this
    /// is the only warning a player gets that their betting time is running out. Without it the timer is invisible
    /// and the auto-deal looks like the table dealing at random.
    ///
    /// IMPORTANT (same rule as TurnPopup / InsurancePopup): put this controller on an ALWAYS-ACTIVE object (TableHUD)
    /// and assign <see cref="panel"/> = the Bet_Popup visual, which it activates/deactivates. The visual may be
    /// disabled by default — a disabled object gets no Update, so the watcher can't live on the popup itself.
    /// <c>Khela ▸ Table ▸ Create Bet Timer Popup</c> builds and wires the whole thing from the existing Turn_Popup.
    /// </summary>
    public sealed class BetTimerPopup : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] private TableController table;
        [Tooltip("The table view — holds the popup until the round-end ceremony (reveal → collect → pay → finale) has " +
                 "finished, so it doesn't appear over the payout. Optional (null = pops straight off the board).")]
        [SerializeField] private BlackjackTableView view;

        [Header("Refs")]
        [Tooltip("The popup VISUAL that slides + is shown/hidden. Put on a SEPARATE object from this controller; it may be disabled by default.")]
        [SerializeField] private RectTransform panel;
        [Tooltip("Countdown label, shown as mm:ss + \"s\" (e.g. 00:12s).")]
        [SerializeField] private TMP_Text timerLabel;
        [Tooltip("Optional caption (\"Place Your Bets\"). Left alone if unassigned.")]
        [SerializeField] private TMP_Text captionLabel;
        [SerializeField] private string caption = "Place Your Bets";

        [Header("Slide")]
        [Tooltip("Slide in from the TOP (park above the shown spot, drop down into view). Uncheck to slide up from below.")]
        [SerializeField] private bool slideFromTop = true;
        [Tooltip("How far OFF-SCREEN to park when hidden (anchored units) — direction set by Slide From Top. Tune so it's fully off-screen.")]
        [SerializeField] private float slideDistance = 1000f;
        [SerializeField] private float slideDuration = 0.35f;
        [Tooltip("Juice: overshoot bounce on the slide. 0 = clean, ~1.7 = ~10% overshoot, higher = bouncier.")]
        [SerializeField] private float overshoot = 1.7f;

        [Header("Urgency")]
        [Tooltip("Tint the countdown once this many seconds remain. 0 disables the tint.")]
        [SerializeField] private float urgentBelowSeconds = 5f;
        [SerializeField] private Color urgentColor = new Color(1f, 0.35f, 0.3f);

        /// <summary>
        /// The betting window just opened for THIS player — raised as the countdown slides in, once per window.
        /// Not raised for a window this player has already committed to, and not while the round-end ceremony is
        /// still playing (see <see cref="Show"/> for why a board field is the wrong thing to watch).
        /// </summary>
        public event System.Action WindowOpened;

        /// <summary>
        /// The betting countdown crossed (or came back over) <see cref="urgentBelowSeconds"/> — the same flag that
        /// recolours the timer label. True = urgent, false = calm or the window closed. Always ends on a false, so a
        /// looping sound driven by this can never be stranded on.
        ///
        /// Deliberately a SECONDS threshold, not the turn timer's fraction-of-duration: the board carries
        /// TurnDurationSeconds but no betting equivalent, and the betting deadline is a ceiling the server renews, so
        /// there is no honest total to take a fraction of here.
        /// </summary>
        public event System.Action<bool> UrgencyChanged;

        private bool _urgent;
        private Vector2 _shownPos;
        private Vector2 _hiddenPos;
        private bool _shown;
        private bool _selfHosted;    // panel is on this same object (fallback): can't deactivate without killing us
        private Coroutine _slide;
        private Color _normalColor;
        private bool _hasNormalColor;

        // Latched for the CURRENT betting window: once we've shown, a stray card tween (the sweep to the discard
        // pile finishing late) must not blink us away. RoundEndSettling includes AnyCardAnimating(), which can
        // re-latch after the director has already finished, so gate the FIRST appearance on it and nothing after.
        private bool _windowShown;

        private void Awake()
        {
            if (panel == null) panel = transform as RectTransform;
            if (panel == null)
            {
                // Hand-added to a non-UI object with nothing assigned. Fail loudly and inert rather than NRE-ing every
                // frame — run Khela ▸ Table ▸ Create Bet Timer Popup, which builds and wires the whole thing.
                Debug.LogError("[BetTimerPopup] No 'panel' assigned and this object has no RectTransform — disabling. " +
                               "Run Khela ▸ Table ▸ Create Bet Timer Popup to build it from the existing Turn_Popup.", this);
                enabled = false;
                return;
            }
            _shownPos = panel.anchoredPosition;                 // designed = SHOWN position (read even while inactive)
            _hiddenPos = _shownPos + (slideFromTop ? Vector2.up : Vector2.down) * slideDistance;

            _selfHosted = panel.gameObject == gameObject;
            if (_selfHosted)
            {
                Debug.LogWarning("[BetTimerPopup] panel is on the same object as the controller — it will hide by sliding " +
                                 "off-screen, not by deactivating. Put the controller on a separate always-active object.");
                panel.anchoredPosition = _hiddenPos;
            }
            else
            {
                panel.gameObject.SetActive(false);              // hidden by default; the controller shows it on demand
            }

            if (timerLabel != null) { _normalColor = timerLabel.color; _hasNormalColor = true; }
            if (captionLabel != null && !string.IsNullOrEmpty(caption)) captionLabel.text = caption;
        }

        private void OnEnable()
        {
            if (table == null) table = FindAnyObjectByType<TableController>();
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();
            if (table == null) { Debug.LogWarning("[BetTimerPopup] No TableController assigned."); return; }
            table.OnBoardChanged += Apply;
            Apply(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= Apply;
            ClearUrgency();   // torn down mid-window — ApplyUrgency will never run again to release it
        }

        private void Update()
        {
            // The window opens and closes BETWEEN board pushes (the round-end ceremony ends locally, with no server
            // message), so this can't be board-driven alone — re-evaluate every frame, exactly like TurnPopup does
            // for the deal-landed gate.
            Apply(table != null ? table.Board : null);

            if (!_shown) return;
            // The label is optional here on purpose — ApplyUrgency also drives the urgency SOUND, and a popup with no
            // countdown text authored should still go urgent.
            if (timerLabel != null) timerLabel.text = Format(Remaining(table != null ? table.Board : null));
            ApplyUrgency();
        }

        /// <summary>Seconds left in the betting window, CLAMPED to the configured length. The server stamps the
        /// deadline as a generous ceiling (it includes an allowance for the round-end ceremony) until our
        /// /presented call collapses it, so without the clamp we'd show a number far larger than a real window.</summary>
        private static double Remaining(BoardSnapshot board)
        {
            if (board?.BettingExpiresAt == null) return 0d;
            double left = (board.BettingExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
            if (board.BettingDurationSeconds > 0) left = Math.Min(left, board.BettingDurationSeconds);
            return left < 0d ? 0d : left;
        }

        // mm:ss + "s", e.g. 12.3s → "00:12s". Ceil so the first whole second shows the full value.
        private static string Format(double seconds)
        {
            if (seconds < 0d) seconds = 0d;
            int total = Mathf.CeilToInt((float)seconds);
            return $"{total / 60:00}:{total % 60:00}s";
        }

        private void ApplyUrgency()
        {
            if (urgentBelowSeconds <= 0f) return;
            bool urgent = _shown && Remaining(table != null ? table.Board : null) <= urgentBelowSeconds;

            // Announced BEFORE the colour, and independently of whether a label or its colour was ever authored: this
            // same flag drives the urgency sound, and gating it on the label would make the audio depend on a visual
            // this popup does not necessarily have. Edge only — ApplyUrgency runs every frame.
            if (urgent != _urgent)
            {
                _urgent = urgent;
                UrgencyChanged?.Invoke(urgent);
            }

            if (timerLabel == null || !_hasNormalColor) return;
            timerLabel.color = urgent ? urgentColor : _normalColor;
        }

        /// <summary>Drop the urgency if it is up. The single release point, so no path can leave a loop playing.</summary>
        private void ClearUrgency()
        {
            if (!_urgent) return;
            _urgent = false;
            UrgencyChanged?.Invoke(false);
        }

        private void Apply(BoardSnapshot board)
        {
            bool windowOpen = board != null && !board.RoundInProgress
                              && board.BettingExpiresAt.HasValue
                              && table != null && table.MySeat > 0
                              && !table.AmIIdleKickWarned    // idle-kick warning takes over the same clock while it's up
                              // Once THIS player has committed (Deal / Repeat) they are done — the shared window is
                              // still running for the other seats, but showing them a countdown they can no longer
                              // act on is what makes the table feel hung.
                              && !table.BettingCommitted;

            if (!windowOpen)
            {
                _windowShown = false;      // window closed (round started, or the server disarmed it) — re-arm
                if (_shown) Hide();
                return;
            }

            // Hold the FIRST appearance until the round-end ceremony has played out, so we don't slide up over the
            // payout — and until then the deadline is still the uncollapsed ceiling, so the number would be wrong.
            if (!_windowShown && view != null && view.RoundEndSettling) return;

            _windowShown = true;
            if (!_shown) Show();
        }

        private void Show()
        {
            _shown = true;
            // The betting window has visibly OPENED — this is the one moment it does. Deliberately raised from here
            // rather than from a board field: BettingExpiresAt is renewed by the server DURING a window, so watching
            // it change would re-announce mid-window, and it is armed before the round-end ceremony has finished, so
            // watching it arm would announce over the payout. Apply() above already resolves both, plus "this player
            // has already committed". Anything hung off this fires exactly when the player sees the clock arrive.
            WindowOpened?.Invoke();
            if (captionLabel != null && !string.IsNullOrEmpty(caption)) captionLabel.text = caption;
            if (timerLabel != null && _hasNormalColor) timerLabel.color = _normalColor;
            if (!_selfHosted && !panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
            panel.anchoredPosition = _hiddenPos;               // start off-screen (top by default), then slide in
            StartSlide(_shownPos, deactivateAtEnd: false, showing: true);
        }

        private void Hide()
        {
            _shown = false;
            ClearUrgency();   // the window closed — the clock stopped, so the urgency stops with it
            StartSlide(_hiddenPos, deactivateAtEnd: !_selfHosted, showing: false);
        }

        private void StartSlide(Vector2 target, bool deactivateAtEnd, bool showing)
        {
            if (_slide != null) StopCoroutine(_slide);
            _slide = StartCoroutine(SlideTo(target, deactivateAtEnd, showing));
        }

        private IEnumerator SlideTo(Vector2 target, bool deactivateAtEnd, bool showing)
        {
            Vector2 start = panel.anchoredPosition;
            float t = 0f;
            while (t < slideDuration && slideDuration > 0f)
            {
                t += Time.unscaledDeltaTime;
                float raw = Mathf.Clamp01(t / slideDuration);
                // Juicy: overshoot IN (EaseOutBack), wind-up-then-shoot OUT (EaseInBack); LerpUnclamped lets the panel
                // briefly pass the endpoints for the bounce. overshoot 0 ⇒ clean cubic eases (no bounce).
                float k = showing ? UITween.EaseOutBack(raw, overshoot) : UITween.EaseInBack(raw, overshoot);
                panel.anchoredPosition = Vector2.LerpUnclamped(start, target, k);
                yield return null;
            }
            panel.anchoredPosition = target;
            _slide = null;
            if (deactivateAtEnd) panel.gameObject.SetActive(false);
        }
    }
}
