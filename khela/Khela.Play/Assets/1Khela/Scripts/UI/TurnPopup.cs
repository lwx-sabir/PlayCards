using System;
using System.Collections;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The "Your Turn" popup. Slides up while it's the LOCAL player's turn (the same window the camera closes in for
    /// and the other-player avatars fade out for) and slides away when the turn passes or the round ends. Shows the
    /// turn countdown as mm:ss + "s" (e.g. 00:15s). View-only — actions go through the action bar; this just signals.
    ///
    /// IMPORTANT (same rule as InsurancePopup): put this controller on an ALWAYS-ACTIVE object (e.g. TableHUD) and
    /// assign <see cref="panel"/> = the Turn_Popup visual, which it activates/deactivates. The visual may be disabled
    /// by default — a disabled object gets no Update, so the watcher can't live on the popup itself.
    /// </summary>
    public sealed class TurnPopup : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] private TableController table;
        [Tooltip("The table view — holds the prompt until MY cards AND the dealer's have landed (the dealer deals last, " +
                 "so that's the whole deal finished). Optional (null = pops straight off the board).")]
        [SerializeField] private BlackjackTableView view;

        [Header("Refs")]
        [Tooltip("The popup VISUAL that slides + is shown/hidden. Put on a SEPARATE object from this controller; it may be disabled by default.")]
        [SerializeField] private RectTransform panel;
        [Tooltip("Turn countdown label, shown as mm:ss + \"s\" (e.g. 00:15s).")]
        [SerializeField] private TMP_Text timerLabel;
        [Tooltip("Optional radial countdown — the Image on the widget's playing frame (Image Type = Filled, Fill " +
                 "Method = Radial 360). Drains full → empty across your turn, matching the ring on the other " +
                 "players' seat avatars. Leave empty for a text-only timer.")]
        [SerializeField] private Image countdownFill;

        [Header("Running out")]
        [Tooltip("Image recoloured once the turn is running out — usually the countdown ring itself, but it can be " +
                 "any graphic (the frame, a glow). Leave empty to disable. Its AUTHORED colour is captured at start " +
                 "and restored on the next turn, so don't drive this image's colour from anywhere else.")]
        [SerializeField] private Image warnImage;
        [Tooltip("Colour it turns once past the threshold.")]
        [SerializeField] private Color warnColor = new Color(0.9f, 0.2f, 0.2f, 1f);
        [Tooltip("Fraction of the turn REMAINING at which it switches — 0.5 = half the time gone.")]
        [Range(0f, 1f)]
        [SerializeField] private float warnBelow = 0.5f;

        [Tooltip("ON = the warn image also BREATHES once past the threshold — a soft alpha pulse, the same effect the " +
                 "felt signs use, so it reads as urgency rather than a static colour change. OFF = colour only.")]
        [SerializeField] private bool warnBreathe = true;
        [Tooltip("Seconds for one full fade-out-and-back. Shorter = more urgent.")]
        [SerializeField] private float breathePeriod = 0.8f;
        [Tooltip("How far down the pulse dips. 1 = no dip, 0 = fades right out.")]
        [Range(0f, 1f)]
        [SerializeField] private float breatheMinAlpha = 0.35f;

        /// <summary>
        /// The countdown crossed (or came back over) its warning threshold — the same flag that recolours the warn
        /// image, so anything hung off it is in step with the visual by construction. True = urgent, false = calm or
        /// the timer stopped. Always ends on a false: the popup closing, or being disabled outright, both release it,
        /// so a looping sound driven by this can never be stranded on.
        /// </summary>
        public event System.Action<bool> UrgencyChanged;

        private bool _warning;
        private Color _warnBaseColor = Color.white;

        [Header("Slide")]
        [Tooltip("Slide in from the TOP (park above the shown spot, drop down into view). Uncheck to slide up from below.")]
        [SerializeField] private bool slideFromTop = true;
        [Tooltip("How far OFF-SCREEN to park when hidden (anchored units) — direction set by Slide From Top. Tune so it's fully off-screen.")]
        [SerializeField] private float slideDistance = 1000f;
        [SerializeField] private float slideDuration = 0.35f;
        [Tooltip("Juice: overshoot bounce on the slide. 0 = clean, ~1.7 = ~10% overshoot, higher = bouncier.")]
        [SerializeField] private float overshoot = 1.7f;

        private Vector2 _shownPos;
        private Vector2 _hiddenPos;
        private bool _shown;
        private bool _selfHosted;    // panel is on this same object (fallback): can't deactivate without killing us
        private Coroutine _slide;

        private void Awake()
        {
            if (panel == null) panel = transform as RectTransform;
            // Capture the authored colour BEFORE anything recolours it — this is what a fresh turn resets to.
            if (warnImage != null) _warnBaseColor = warnImage.color;
            _shownPos = panel.anchoredPosition;                 // designed = SHOWN position (read even while inactive)
            _hiddenPos = _shownPos + (slideFromTop ? Vector2.up : Vector2.down) * slideDistance;

            _selfHosted = panel.gameObject == gameObject;
            if (_selfHosted)
            {
                Debug.LogWarning("[TurnPopup] panel is on the same object as the controller — it will hide by sliding " +
                                 "off-screen, not by deactivating. Put the controller on a separate always-active object.");
                panel.anchoredPosition = _hiddenPos;
            }
            else
            {
                panel.gameObject.SetActive(false);              // hidden by default; the controller shows it on demand
            }
        }

        private void OnEnable()
        {
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();   // so the deal-landed gate can't be silently bypassed
            if (table == null) { Debug.LogWarning("[TurnPopup] No TableController assigned."); return; }
            table.OnBoardChanged += Apply;
            Apply(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= Apply;
            ClearWarning();   // torn down mid-countdown — Tick will never run again to release it
        }

        /// <summary>Drop the urgency if it is up. The single release point, so no path can leave a loop playing.</summary>
        private void ClearWarning()
        {
            if (!_warning) return;
            _warning = false;
            UrgencyChanged?.Invoke(false);
        }

        private bool _lastReady;

        private void Update()
        {
            // Cards land BETWEEN board pushes, so re-evaluate when my decision inputs (my cards + the dealer's) settle —
            // Apply is board-driven, so without this the prompt would never appear for a turn that started mid-deal.
            if (table != null && view != null)
            {
                bool ready = view.DecisionReady(table.MySeat);
                if (ready != _lastReady) { _lastReady = ready; Apply(table.Board); }
            }

            // NB: not gated on timerLabel any more — the ring below is a valid countdown on its own, so a widget with
            // no text still animates.
            if (!_shown) return;
            var board = table != null ? table.Board : null;
            var exp = board != null ? board.TurnExpiresAt : null;
            double remaining = exp.HasValue ? (exp.Value - DateTimeOffset.UtcNow).TotalSeconds : 0d;

            // Clamp to a real turn — the deadline is a generous ceiling until our /presented call collapses it.
            float turnSeconds = board != null ? board.TurnDurationSeconds : 0f;
            if (turnSeconds > 0f) remaining = Math.Min(remaining, turnSeconds);
            if (remaining < 0d) remaining = 0d;

            if (timerLabel != null) timerLabel.text = Format(remaining);

            // Radial ring drains with the clock — the same widget the opponents' seat avatars use, so your own
            // countdown reads identically to theirs. Full when there's no turn length to divide by, rather than
            // empty: an unarmed ring should look idle, not expired.
            float left01 = turnSeconds > 0f ? Mathf.Clamp01((float)(remaining / turnSeconds)) : 1f;

            if (countdownFill != null) countdownFill.fillAmount = left01;

            // Running out. Driven off the same fraction as the ring so the colour flips exactly when the ring passes
            // the mark, and assigned every frame rather than on the edge — cheap, and it self-corrects if anything
            // else touches the colour.
            bool warning = left01 < warnBelow;

            // Computed OUTSIDE the warnImage null check on purpose: this same flag drives the urgency sound, and a
            // table with no warn image authored should still get the audio. Raised on the EDGE only — Tick runs every
            // frame, and a looping sound restarted 60 times a second is a buzz, not a loop.
            if (warning != _warning)
            {
                _warning = warning;
                UrgencyChanged?.Invoke(warning);
            }

            if (warnImage != null)
            {
                var c = warning ? warnColor : _warnBaseColor;

                // Breathe on the warn colour's OWN alpha (multiply, don't overwrite) so an authored translucency is
                // preserved instead of the pulse snapping it to opaque. Sine, so there's no hard edge at either end.
                if (warning && warnBreathe && breathePeriod > 0f)
                {
                    float u = (Mathf.Sin(Time.unscaledTime * (2f * Mathf.PI / breathePeriod)) + 1f) * 0.5f;
                    c.a *= Mathf.Lerp(breatheMinAlpha, 1f, u);
                }

                warnImage.color = c;
            }
        }

        // Seconds only, e.g. 15.3s → "15s". Ceil so the first whole second shows the full value. Two digits keeps the
        // label a constant width as it counts down, so the number doesn't shuffle sideways inside the ring at 9→8.
        private static string Format(double seconds)
        {
            if (seconds < 0d) seconds = 0d;
            int total = Mathf.CeilToInt((float)seconds);
            return $"{total:00}s";
        }

        private void Apply(BoardSnapshot board)
        {
            // Gate on the ANIMATION: only prompt once MY cards AND the DEALER's have landed — the dealer is dealt LAST,
            // so that means the whole deal has finished. Without this the prompt pops the instant the server says it's
            // my turn, while the dealer's second/hole card is still in the air.
            bool myTurn = table != null && table.IsMyTurn
                          && (view == null || view.DecisionReady(table.MySeat));
            if (myTurn) { if (!_shown) Show(); }
            else        { if (_shown) Hide(); }
        }

        private void Show()
        {
            _shown = true;
            if (!_selfHosted && !panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
            if (countdownFill != null) countdownFill.fillAmount = 1f;   // start whole, not on last turn's leftover
            if (warnImage != null) warnImage.color = _warnBaseColor;    // and not still red from the last one
            panel.anchoredPosition = _hiddenPos;               // start off-screen, then slide in
            StartSlide(_shownPos, deactivateAtEnd: false, showing: true);
        }

        private void Hide()
        {
            _shown = false;
            ClearWarning();   // the turn ended — the clock stopped, so the urgency stops with it
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
