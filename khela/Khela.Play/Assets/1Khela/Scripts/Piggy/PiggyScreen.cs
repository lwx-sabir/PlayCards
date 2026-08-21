using System;
using System.Collections;
using Khela.Common.Piggy;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Piggy
{
    /// <summary>
    /// The piggy popup's content: two views — FILLING and FULL — bound to the server's snapshot.
    ///
    /// Both views hold the same kinds of thing (an amount, a line of copy, the buy buttons), so this binds each set
    /// separately rather than assuming one layout. Everything is optional: a half-authored view still runs, because a
    /// missing label should cost you a label, not the screen.
    ///
    /// It decides nothing about money. Which offers exist, what they pay and whether the bank may be bought at all
    /// are the server's answers; this renders them and reports which button was pressed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PiggyScreen : MonoBehaviour
    {
        [Serializable]
        public sealed class View
        {
            [Tooltip("The whole view object — Popup_Piggy_Progressing or Popup_Piggy_Full.")]
            public GameObject root;

            [Tooltip("The banked amount.")]
            public TMP_Text valueText;

            [Tooltip("Optional. The tier's capacity, for a '5,000,000 / 10,000,000' reading. Unassigned is ignored, " +
                     "so a single-number layout needs no placeholder.")]
            public TMP_Text targetText;

            [Tooltip("Optional line of copy under the amount — the offer, in words.")]
            public TMP_Text infoText;

            [Tooltip("Progress toward full. On the filling view this is the bank; on the full view it is usually the " +
                     "countdown draining instead — assign whichever this view actually shows.")]
            public Slider bar;
            public Image barFill;

            [Tooltip("What the bar measures here. Bank = how full the piggy is. Time = how much of the window is left.")]
            public BarMeaning barShows;

            [Tooltip("The countdown value, e.g. '23h 59m'.")]
            public TMP_Text timeText;
            [Tooltip("Objects to hide when no countdown is running — the label, the icon, the pill behind them.")]
            public GameObject[] timeObjects;

            [Tooltip("Buy the bank as it stands (full) or its capacity (filling).")]
            public Button collectButton;
            [Tooltip("Buy double. Leave the object inactive in a view that shouldn't offer it.")]
            public Button doubleButton;
        }

        public enum BarMeaning
        {
            /// <summary>Fraction of capacity banked.</summary>
            Bank = 0,
            /// <summary>Fraction of the window still left — drains toward the deadline.</summary>
            Time = 1,
        }

        [Header("Views — exactly one is shown at a time")]
        [SerializeField] private View filling;
        [SerializeField] private View full;

        [Header("Busy — the purchase round trip and the whole break payoff")]
        [Tooltip("Popup_Piggy_busy. A ROOT, not a View, deliberately: nothing in it is painted from the server " +
                 "snapshot. Its amount, its status line and its pig are driven entirely by PiggyBreakDirector, and " +
                 "painting it from here as well would mean two writers fighting over the same value label.\n\n" +
                 "It wins over both other views while it is up, because the break animation lives inside it.")]
        [SerializeField] private GameObject busyRoot;

        [Header("Bar intro")]
        [Tooltip("On OPEN, run the bar up from empty to its real value instead of showing it already there. It is the " +
                 "one moment the player is looking straight at their progress, and a bar that arrives full says " +
                 "nothing about how it got there. 0 = no intro.")]
        [SerializeField] private float barIntroSeconds = 0.7f;
        [Tooltip("Beat before the run starts, so the panel has finished arriving first — two things moving at once " +
                 "read as one busy motion rather than two.")]
        [SerializeField] private float barIntroDelay = 0.15f;

        [Header("Format")]
        [SerializeField] private string moneyFormat = "#,0";
        [Tooltip("{0} = the remaining time, already formatted (2d 4h / 5h 12m / 48m).")]
        [SerializeField] private string timeFormat = "{0}";

        [Header("Close")]
        [Tooltip("Every close affordance — the X on both views, and the dim behind them if it should dismiss.")]
        [SerializeField] private Button[] closeButtons;

        /// <summary>The player chose an offer. The panel turns this into a purchase; this screen never does.</summary>
        public event Action<PiggyBreakOption> BreakRequested;

        /// <summary>They asked to close.</summary>
        public event Action CloseRequested;

        /// <summary>
        /// Show the FULL view whatever the snapshot says - the test affordance behind PiggyPanel's Test Mode.
        ///
        /// Driven through the SAME branch the real thing uses rather than by switching the hidden view on behind
        /// this component's back: a view force-activated from outside would be re-hidden by the next render, and
        /// its buttons and bar would be whatever the last real paint left there.
        /// </summary>
        public bool ForceFull { get; set; }

        /// <summary>Is the busy view up? Set through <see cref="SetBusy"/>.</summary>
        public bool Busy { get; private set; }

        /// <summary>
        /// Show or hide the busy view. On from the moment a purchase is requested until the payoff has finished —
        /// which is a good deal longer than the network call, because the break animation plays inside it.
        /// </summary>
        public void SetBusy(bool on)
        {
            if (Busy == on) return;
            Busy = on;

            if (_state != null) Render(_state);
            else Show(busyRoot, on);
        }

        /// <summary>
        /// Lock or release the buy buttons across both views.
        ///
        /// The store sheet takes a moment to appear, and a live buy button in that window is a second purchase the
        /// player did not mean to make. Locked on request, released only when the flow has actually resolved.
        /// </summary>
        public void SetBuyInteractable(bool on)
        {
            Set(filling); Set(full);

            void Set(View v)
            {
                if (v == null) return;
                if (v.collectButton != null) v.collectButton.interactable = on;
                if (v.doubleButton != null) v.doubleButton.interactable = on;
            }
        }

        private void Awake()
        {
            _blast = GetComponent<PiggyBlast>();
            _director = GetComponent<PiggyBreakDirector>();

            Wire(filling, PiggyBreakOption.Early, PiggyBreakOption.Early);
            Wire(full, PiggyBreakOption.Full, PiggyBreakOption.FullDouble);

            if (closeButtons != null)
                foreach (var b in closeButtons)
                    if (b != null) b.onClick.AddListener(() => CloseRequested?.Invoke());
        }

        private void Wire(View view, PiggyBreakOption main, PiggyBreakOption second)
        {
            if (view == null) return;
            if (view.collectButton != null) view.collectButton.onClick.AddListener(() => BreakRequested?.Invoke(main));
            if (view.doubleButton != null) view.doubleButton.onClick.AddListener(() => BreakRequested?.Invoke(second));
        }

        private void OnEnable()
        {
            PiggyState.Instance.Changed += Render;
            if (_blast != null) _blast.Finished += PaintHeld;
            if (_director != null) _director.Finished += PaintHeld;
            if (PiggyState.Instance.Current != null) Render(PiggyState.Instance.Current);

            _ticker = StartCoroutine(Tick());
        }

        private void OnDisable()
        {
            PiggyState.Instance.Changed -= Render;
            if (_blast != null) _blast.Finished -= PaintHeld;
            if (_director != null) _director.Finished -= PaintHeld;
            if (_ticker != null) { StopCoroutine(_ticker); _ticker = null; }
            if (_intro != null) { StopCoroutine(_intro); _intro = null; }
        }

        /// <summary>Draw a snapshot. Safe with null or a disabled feature — both views simply hide.</summary>
        public void Render(PiggyStateDto state)
        {
            // BUSY IS DECIDED FIRST, before the playing guard below.
            //
            // It has to be, and getting this order wrong cost a whole debugging session: the director sets
            // IsPlaying BEFORE its coroutine runs, and the coroutine's first act is to raise this view. If the
            // guard ran first it swallowed that switch, so the busy view never appeared and the entire break -
            // charge, bang, particles, debris - played inside a hidden hierarchy. The chips still flew, because
            // they render in their own layer, and the view only appeared at the very end when IsPlaying went false.
            //
            // Busy is safe to honour mid-payoff precisely because it is NOT derived from the snapshot: it says
            // which view is up, not what the bank is worth.
            if (Busy)
            {
                Show(filling?.root, false);
                Show(full?.root, false);
                Show(busyRoot, true);
                _held = state;      // keep the newest snapshot for whenever we come back out
                return;
            }
            Show(busyRoot, false);

            // A running blast owns the screen. The break that starts one also EMPTIES the bank, so the refresh that
            // follows a second later says "not full" and would hide the full view - taking the pig and the flying
            // debris with it, mid-explosion. Hold the snapshot and paint it when the pieces have died. The DIRECTOR
            // counts too: the receipt and the chips outlive the debris by seconds.
            if ((_blast != null && _blast.IsPlaying) || (_director != null && _director.IsPlaying))
            {
                _held = state;
                return;
            }

            _state = state;

            if (state == null || !state.Enabled)
            {
                Show(filling?.root, false);
                Show(full?.root, false);
                return;
            }

            bool ready = state.CanBreak || ForceFull;
            Show(filling?.root, !ready);
            Show(full?.root, ready);

            _secondsLeft = state.SecondsLeft;
            Paint(ready ? full : filling, state);
        }

        private void Paint(View view, PiggyStateDto state)
        {
            if (view == null) return;

            if (view.valueText != null) view.valueText.text = state.Amount.ToString(moneyFormat);
            if (view.targetText != null) view.targetText.text = state.Max.ToString(moneyFormat);

            // The bar means different things in the two views, and only the author knows which — a full bank's
            // progress bar is pinned at 100% and says nothing, while its countdown says everything.
            float value = view.barShows == BarMeaning.Time ? TimeFraction(state) : Mathf.Clamp01(state.Percent);

            // On the first paint after opening, RUN the bar up to that value rather than showing it already there.
            // Only the first: every later paint is a correction, and a bar that re-runs on each refresh looks like the
            // number changed when it didn't.
            //
            // Progress bars only. A countdown bar filling UP would animate the opposite of what it means — the window
            // is draining, and the one thing it must never look like on open is time being handed back.
            bool canIntro = view.barShows == BarMeaning.Bank && (view.bar != null || view.barFill != null);

            // Kept live rather than captured: the intro reads it EVERY frame, so the server's answer landing
            // mid-run retargets the bar instead of being ignored or snapping it to the end.
            _barTarget = value;

            if (_introPending && canIntro && barIntroSeconds > 0f)
            {
                _introPending = false;
                if (_intro != null) StopCoroutine(_intro);
                _intro = StartCoroutine(RunBar(view));
            }
            else
            {
                // Consumed either way: an intro that could not run on THIS view must not lie in wait for the next
                // paint, or a routine refresh would suddenly animate.
                _introPending = false;

                // While the intro is running its value is the authority; a refresh landing mid-run must not snap the
                // bar to the end and throw the animation away.
                if (_intro == null) SetBar(view, value);
            }

            // The countdown exists only once the player has been shown a full bank, so its absence is a state, not a
            // zero — hide it rather than showing 0, which reads as "expired".
            ShowTime(view, state.TimerRunning);
            PaintTime(view);
        }

        /// <summary>
        /// How much of the window is left, 0..1 — straight from the server's own window length.
        ///
        /// Not inferred from the largest remaining time this client has happened to see: that starts wrong for anyone
        /// who opens the game halfway through, which is most people.
        /// </summary>
        private float TimeFraction(PiggyStateDto state)
        {
            if (!state.TimerRunning || state.WindowSeconds <= 0) return 1f;
            return Mathf.Clamp01((float)_secondsLeft / state.WindowSeconds);
        }

        /// <summary>
        /// Arm the bar intro. Called by the panel as it opens, not by <see cref="Render"/>: the intro belongs to the
        /// act of opening, and the first render can happen for other reasons — a cached paint, a refresh landing.
        /// </summary>
        public void ArmIntro()
        {
            _introPending = true;
            if (_intro != null) { StopCoroutine(_intro); _intro = null; }

            // Start from empty so the run has somewhere to come from, even if the panel was closed mid-run last time.
            SetBar(filling, 0f);
            SetBar(full, 0f);
        }

        private IEnumerator RunBar(View view)
        {
            SetBar(view, 0f);

            if (barIntroDelay > 0f) yield return new WaitForSecondsRealtime(barIntroDelay);

            float t = 0f;
            while (t < barIntroSeconds)
            {
                t += Time.unscaledDeltaTime;
                // Decelerating, so it arrives rather than stops: the last tenth is where the eye reads the value.
                SetBar(view, Mathf.Lerp(0f, _barTarget, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / barIntroSeconds))));
                yield return null;
            }

            SetBar(view, _barTarget);
            _intro = null;
        }

        private static void SetBar(View view, float value)
        {
            if (view == null) return;
            // normalizedValue, not value: the fraction must land on the slider's AUTHORED range. Driving .value with
            // a 0..1 fraction on a slider authored 0..120 paints under 1% of the track — an invisible bar.
            if (view.bar != null) view.bar.normalizedValue = value;
            if (view.barFill != null) view.barFill.fillAmount = value;
        }

        private IEnumerator Tick()
        {
            var second = new WaitForSecondsRealtime(1f);
            while (true)
            {
                yield return second;
                if (_secondsLeft <= 0) continue;

                _secondsLeft--;
                var view = _state != null && _state.CanBreak ? full : filling;
                PaintTime(view);
                if (view != null && view.barShows == BarMeaning.Time && _state != null) Paint(view, _state);

                // Hit zero: the bank is gone as far as this client knows. Ask rather than guess what replaced it.
                if (_secondsLeft <= 0) _ = PiggyState.Instance.RefreshAsync(force: true);
            }
        }

        private void PaintTime(View view)
        {
            if (view?.timeText == null) return;
            view.timeText.text = string.Format(timeFormat, FormatSpan(_secondsLeft));
        }

        private static void ShowTime(View view, bool on)
        {
            if (view == null) return;
            if (view.timeText != null) Show(view.timeText.gameObject, on);
            if (view.timeObjects == null) return;
            foreach (var go in view.timeObjects) Show(go, on);
        }

        /// <summary>Coarse and readable: days and hours far out, minutes near the end. Nobody reads seconds off a
        /// three-day deadline, and a ticking seconds digit on a slow clock is just noise.</summary>
        private static string FormatSpan(long seconds)
        {
            if (seconds <= 0) return "0m";

            var span = TimeSpan.FromSeconds(seconds);
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
            return $"{span.Seconds}s";
        }

        /// <summary>The blast is over - paint whatever landed while it was running.</summary>
        private void PaintHeld()
        {
            if (_held == null) return;
            var pending = _held;
            _held = null;
            Render(pending);
        }

        private static void Show(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        private PiggyStateDto _state;
        private long _secondsLeft;
        private Coroutine _ticker;

        private PiggyBlast _blast;
        private PiggyBreakDirector _director;
        private PiggyStateDto _held;
        private Coroutine _intro;
        private bool _introPending;
        private float _barTarget;
    }
}
