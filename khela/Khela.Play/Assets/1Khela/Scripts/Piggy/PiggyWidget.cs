using System;
using System.Collections;
using Khela.Common.Piggy;
using PlayCard.UI;
using PlayCard.UI.RewardFly;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Piggy
{
    /// <summary>
    /// The piggy-bank HUD: one prefab, two states — FILLING and READY — driven entirely by the server's snapshot.
    ///
    /// Put it on the widget root (the object holding Title / Text_Value / Progress_View / Available_View). Every field
    /// is optional, so a half-authored prefab still runs rather than throwing on the first frame.
    ///
    /// It owns one piece of behaviour beyond display: when the ready state is first SHOWN, it tells the server, which
    /// is what starts the player's countdown. That call belongs here and nowhere else — the server deliberately keeps
    /// it out of the plain read, because a deadline started by a background refresh would destroy a full bank the
    /// player was never shown.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PiggyWidget : MonoBehaviour
    {
        [Header("States — exactly one is shown at a time")]
        [Tooltip("Shown while the bank is still filling: the bar and the amount.")]
        [SerializeField] private GameObject progressView;
        [Tooltip("Shown once the bank is full and buyable.")]
        [SerializeField] private GameObject availableView;

        [Header("Labels (all optional)")]
        [Tooltip("The amount banked. Formatted with Money Format.")]
        [SerializeField] private TMP_Text valueText;
        [SerializeField] private string moneyFormat = "#,0";

        [Tooltip("The countdown. Hidden unless a clock is actually running — a full bank the player has not been " +
                 "shown yet has no deadline, which is NOT the same as a deadline of zero.")]
        [SerializeField] private TMP_Text timerText;
        [Tooltip("{0} = the remaining time, already formatted (2d 4h / 5h 12m / 48m).")]
        [SerializeField] private string timerFormat = "{0}";
        [Tooltip("Whole objects to hide while no countdown is running — a label, an icon, the pill behind them.")]
        [SerializeField] private GameObject[] timerObjects;

        [Header("Progress")]
        [Tooltip("Filled Image or Slider — either works, whichever the art uses.")]
        [SerializeField] private Image fillImage;
        [SerializeField] private Slider fillSlider;
        [Tooltip("Seconds for the bar to slide to a new value. 0 snaps.")]
        [SerializeField] private float fillLerpSeconds = 0.35f;

        [Header("Chips flying in (the return-from-a-session moment)")]
        [Tooltip("The RewardFly that throws the chips. Leave empty and the bar simply fills — everything below is skipped.")]
        [SerializeField] private RewardFly rewardFly;
        [Tooltip("Where the chips come FROM. EMPTY = this widget, so the coins tumble out of the pig and back into it. " +
                 "Do NOT point it at the chip balance: chips flying off the balance says the player was charged.")]
        [SerializeField] private RectTransform flyFrom;
        [Tooltip("The reward id the burst flies under. Must match the Reward Fly Target on this widget, and must NOT " +
                 "be 'Chips' — that id belongs to the wallet counter, and the pieces would land there instead.")]
        [SerializeField] private string flyRewardId = "Piggy";
        [Tooltip("Artwork for the flying pieces. Empty falls back to the RewardFly's own icon set.")]
        [SerializeField] private Sprite chipIcon;
        [Tooltip("Beat between the celebration being claimed and the chips actually launching — the card auto-opens " +
                 "for this moment and must finish unfolding before pieces fly at the pig. Slightly over the card's " +
                 "open time; also reads as anticipation when the card was already open.")]
        [SerializeField] private float celebrateDelay = 0.45f;
        [Tooltip("How many pieces fly, whatever the amount. 0 lets RewardFly scale the count to the amount — which " +
                 "for a piggy is usually the wrong shape, since a big session should feel like a shower, not a heap.")]
        [SerializeField] private int flyPieces = 14;

        [Header("Life")]
        [Tooltip("Optional. Poked when the bar moves, so the pig reacts to chips landing in it.")]
        [SerializeField] private IdleMotion idle;
        [Tooltip("How hard the pig kicks per chip that lands. Every piece, not just the last — a stream being caught " +
                 "one by one is the whole reason the burst is staggered.")]
        [Range(0f, 1f)][SerializeField] private float pokePerChip = 0.45f;
        [Tooltip("Optional. Fired the first time a snapshot shows the bank ready — a badge, a sting, a nudge.")]
        [SerializeField] private UnityEngine.Events.UnityEvent onBecameReady;

        [Header("Refresh")]
        [Tooltip("Seconds between background refreshes while this widget is visible. The bank only moves when the " +
                 "player finishes a round, so this is a safety net rather than the main path — keep it slack.")]
        [SerializeField] private float refreshSeconds = 60f;

        private void OnEnable()
        {
            PiggyState.Instance.Changed += OnChanged;
            PiggyState.Instance.BecameReady += OnBecameReady;
            RewardFlyTarget.BurstProgress += OnPiece;
            RewardFlyTarget.BurstEnded += OnBurstEnded;

            // Paint whatever is cached before the network answers, so the widget never flashes empty.
            if (PiggyState.Instance.Current != null) Render(PiggyState.Instance.Current);
            _ = PiggyState.Instance.RefreshAsync();

            _ticker = StartCoroutine(Tick());
        }

        private void OnDisable()
        {
            PiggyState.Instance.Changed -= OnChanged;
            PiggyState.Instance.BecameReady -= OnBecameReady;
            RewardFlyTarget.BurstProgress -= OnPiece;
            RewardFlyTarget.BurstEnded -= OnBurstEnded;

            if (_ticker != null) { StopCoroutine(_ticker); _ticker = null; }
            if (_fill != null) { StopCoroutine(_fill); _fill = null; }

            // Closed before the delayed throw ever happened: RELEASE the claim. The chips were never shown and the
            // server was never acked, so leaving the flags set would mean a delta acknowledged-in-spirit that no
            // future open is allowed to celebrate — blocked forever. Undone, the next open celebrates it properly.
            if (_pendingBurst != null)
            {
                StopCoroutine(_pendingBurst);
                _pendingBurst = null;
                _awaitingAck = false;
                _ackFor = 0m;
                _celebrating = false;
                ApplyFill(_shownFill = _celebrationFrom);
            }

            // Leaving mid-burst: the pieces die with the screen, so the bar must not come back half filled.
            if (_celebrating) { _celebrating = false; _shownFill = _celebrationTo; ApplyFill(_shownFill); }
        }

        private void OnChanged(PiggyStateDto state) => Render(state);

        private void OnBecameReady(PiggyStateDto state) => onBecameReady?.Invoke();

        /// <summary>Draw a snapshot. Safe to call with null or a disabled feature — the widget simply hides.</summary>
        public void Render(PiggyStateDto state)
        {
            if (state == null || !state.Enabled)
            {
                Show(progressView, false);
                Show(availableView, false);
                ShowTimer(false);
                return;
            }

            bool ready = state.CanBreak;
            Show(progressView, !ready);
            Show(availableView, ready);

            if (valueText != null) valueText.text = state.Amount.ToString(moneyFormat);

            if (!TryCelebrate(state)) SetFill(state.Percent);

            // The clock is shown off TimerRunning, never off the seconds. Zero seconds with no window means "no
            // deadline yet"; zero seconds with a window means "gone" — and the two must not look the same.
            _secondsLeft = state.SecondsLeft;
            ShowTimer(state.TimerRunning);
            PaintTimer();

            // First sighting of a full bank starts the countdown, server-side. Guarded so re-enabling the widget or a
            // routine refresh doesn't re-post — the server ignores repeats anyway, but there is no reason to ask.
            if (ready && !state.TimerRunning && !_seenPosted)
            {
                _seenPosted = true;
                _ = PiggyState.Instance.MarkSeenAsync();
            }
            if (!ready) _seenPosted = false;   // a new fill deserves a new sighting
        }

        // ---------------- the celebration ----------------

        /// <summary>
        /// The player is back from playing and a real amount went in — throw the chips, and let the bar fill WITH them
        /// rather than ahead of them. Returns true when it took over the bar.
        ///
        /// The delta comes from the SERVER (<c>UnseenAccrued</c>), and that matters: it is a running total that only
        /// resets when a celebration actually plays, so three short sessions add up to one moment instead of three
        /// shrugs — and it survives an app restart, which a client-side baseline does not. With a daily cap smaller
        /// than the threshold, a baseline that resets on launch makes the celebration unreachable.
        /// </summary>
        private bool TryCelebrate(PiggyStateDto state)
        {
            if (_celebrating) return true;                     // already running one; don't stack bursts

            // Waiting for the server to be told about the LAST one.
            //
            // The acknowledgement is posted when the burst ends, but it is a round trip — and until it lands, every
            // refresh still reports the same UnseenAccrued. Without this the widget celebrates the same chips again a
            // minute later, which is what it did. Cleared below the moment the server confirms a smaller delta.
            if (_awaitingAck)
            {
                if (state.UnseenAccrued < _ackFor) { _awaitingAck = false; _ackFor = 0m; }
                else return false;
            }

            var delta = state.UnseenAccrued;
            if (delta <= 0m) return false;
            if (delta < state.MinFlyAmount) return false;      // too small to be a moment — the bar just fills

            // Nothing to throw the chips with: acknowledge anyway, or the delta sits there and every future refresh
            // tries again on a screen that structurally cannot show it.
            if (rewardFly == null || string.IsNullOrWhiteSpace(flyRewardId))
            {
                _awaitingAck = true;
                _ackFor = delta;
                _ = PiggyState.Instance.MarkCelebratedAsync();
                return false;
            }

            // Claimed BEFORE the burst starts, not when it ends: a refresh landing mid-flight would otherwise see the
            // same delta with nothing yet acknowledged and start a second burst on top of this one.
            _awaitingAck = true;
            _ackFor = delta;

            // The bar is handed to the burst: it starts where it was and is walked up by the landings, so the chips
            // are visibly what fills it. Snapped to the start first — a lerp still running from a previous render
            // would otherwise race the pieces.
            if (_fill != null) { StopCoroutine(_fill); _fill = null; }
            _celebrationFrom = _shownFill;
            _celebrationTo = Mathf.Clamp01(state.Percent);
            _celebrating = true;
            ApplyFill(_celebrationFrom);

            _pendingBurst = StartCoroutine(PlayBurstAfterBeat(new RewardFlyItem
            {
                RewardId = flyRewardId,
                Amount = delta,
                Icon = chipIcon,
                Pieces = flyPieces,
                // No authored source falls back to THIS widget — the coins tumble out of the pig and back into it.
                // RewardFly skips any reward with no source at all, so leaving this null would silently cancel the
                // whole burst rather than degrade to something sensible.
                From = flyFrom != null ? flyFrom : (RectTransform)transform,
            }));

            return true;
        }

        /// <summary>
        /// The throw, a beat late. The folded card auto-opens for its celebration, and the pieces must not launch
        /// while the pig they land in is still mid-unfold. Only the THROW waits — the claim flags are set
        /// synchronously in <see cref="TryCelebrate"/>, because the refresh-race guard cannot afford a gap.
        /// </summary>
        private System.Collections.IEnumerator PlayBurstAfterBeat(RewardFlyItem item)
        {
            if (celebrateDelay > 0f) yield return new WaitForSecondsRealtime(celebrateDelay);
            _pendingBurst = null;
            rewardFly.Play(item);
        }

        private Coroutine _pendingBurst;

        /// <summary>One chip landed in the pig: walk the bar a slice and kick the art. Every piece, not just the last.</summary>
        private void OnPiece(string rewardId, float progress01)
        {
            if (!_celebrating || !string.Equals(rewardId, flyRewardId, StringComparison.OrdinalIgnoreCase)) return;

            _shownFill = Mathf.Lerp(_celebrationFrom, _celebrationTo, Mathf.Clamp01(progress01));
            ApplyFill(_shownFill);

            if (idle != null && pokePerChip > 0f) idle.Poke(pokePerChip);
        }

        private void OnBurstEnded(string rewardId)
        {
            if (!string.Equals(rewardId, flyRewardId, StringComparison.OrdinalIgnoreCase)) return;

            // Land on the truth whatever the pieces managed — a burst cut short by leaving the screen must not strand
            // the bar half way.
            _celebrating = false;
            _shownFill = _celebrationTo;
            ApplyFill(_shownFill);

            // The player has now SEEN it, so the next celebration measures from here. Sent at the end rather than the
            // start: if the app dies mid-burst the delta survives and they get their moment next time.
            _ = PiggyState.Instance.MarkCelebratedAsync();
        }

        // ---------------- the bar ----------------

        private void SetFill(float target)
        {
            target = Mathf.Clamp01(target);

            if (fillLerpSeconds <= 0f || !isActiveAndEnabled)
            {
                ApplyFill(target);
                _shownFill = target;
                return;
            }

            if (Mathf.Approximately(target, _shownFill)) return;

            if (_fill != null) StopCoroutine(_fill);
            _fill = StartCoroutine(LerpFill(target));
        }

        private IEnumerator LerpFill(float target)
        {
            float from = _shownFill;
            float t = 0f;

            while (t < fillLerpSeconds)
            {
                t += Time.unscaledDeltaTime;
                float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / fillLerpSeconds));
                _shownFill = Mathf.Lerp(from, target, u);
                ApplyFill(_shownFill);
                yield return null;
            }

            _shownFill = target;
            ApplyFill(target);
            _fill = null;

            // The pig reacts to the chips arriving, not to the number changing — so it only kicks when the bar
            // actually grew, never when a refresh corrects it downward after an expiry.
            if (target > from && idle != null) idle.Poke(Mathf.Clamp01((target - from) * 6f));
        }

        private void ApplyFill(float value)
        {
            if (fillImage != null) fillImage.fillAmount = value;
            // normalizedValue, not value: the fraction must land on the slider's AUTHORED range. Driving .value with
            // a 0..1 fraction on a slider authored 0..120 paints under 1% of the track — an invisible bar.
            if (fillSlider != null) fillSlider.normalizedValue = value;
        }

        // ---------------- the countdown ----------------

        /// <summary>
        /// Ticks the countdown locally and re-reads the bank occasionally.
        ///
        /// Counting down locally rather than polling matters: the deadline is hours away, so a per-second request
        /// would be thousands of round trips to watch a number the client can compute. The refresh exists only to
        /// catch what the client cannot know — a round settled on another device, or a window the server expired.
        /// </summary>
        private IEnumerator Tick()
        {
            var second = new WaitForSecondsRealtime(1f);
            float sinceRefresh = 0f;

            while (true)
            {
                yield return second;
                sinceRefresh += 1f;

                if (_secondsLeft > 0)
                {
                    _secondsLeft--;
                    PaintTimer();

                    // Hit zero: the bank is gone as far as this client knows. Ask the server rather than guessing
                    // what replaced it.
                    if (_secondsLeft <= 0) { sinceRefresh = 0f; _ = PiggyState.Instance.RefreshAsync(force: true); }
                }

                if (refreshSeconds > 0f && sinceRefresh >= refreshSeconds)
                {
                    sinceRefresh = 0f;
                    _ = PiggyState.Instance.RefreshAsync();
                }
            }
        }

        private void PaintTimer()
        {
            if (timerText == null) return;
            timerText.text = string.Format(timerFormat, FormatSpan(_secondsLeft));
        }

        private void ShowTimer(bool on)
        {
            if (timerText != null) Show(timerText.gameObject, on);
            if (timerObjects == null) return;
            foreach (var go in timerObjects) Show(go, on);
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

        private static void Show(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        private float _shownFill;
        private long _secondsLeft;
        private bool _seenPosted;
        private Coroutine _ticker;
        private Coroutine _fill;

        // The celebration in flight. What has been celebrated is the SERVER's to remember (UnseenAccrued) — a
        // client-side baseline dies with every app restart, which is how the moment gets lost.
        private bool _celebrating;
        private float _celebrationFrom;
        private float _celebrationTo;

        // The delta already thrown chips for, held until the server confirms it. Without it the same chips are
        // celebrated again on the next refresh — the acknowledgement is a round trip, and the state does not change
        // until it lands.
        private bool _awaitingAck;
        private decimal _ackFor;
    }
}
