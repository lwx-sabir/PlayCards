using System.Collections;
using System.Collections.Generic;
using PlayCard.Game.Dtos;     // BoardSnapshot — re-arming the hold each round
using PlayCard.Game.Net;      // WalletBalances
using PlayCard.Game.Table;    // TableController
using PlayCard.Game.Wallet;   // WalletManager
using TMPro;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Juice for the CHIP COUNT: instead of the number snapping, it ROLLS to its new value and the label PUNCHES,
    /// with an optional colour flash (green up / red down).
    ///
    /// Timing is the point of this component. A CREDIT must land on the beat the flying chips actually hit the icon —
    /// not when the server value arrives, which is seconds earlier (the settle push comes in before the dealer has even
    /// revealed). So:
    ///  • DEDUCTIONS (a stake leaving) roll down immediately — that's the tap the player just made.
    ///  • CREDITS are HELD while a <see cref="Game.Table.RoundEndDirector"/> is presenting the round-end, then fed in
    ///    slice by slice by <see cref="WinChipFly"/> as each chip lands (<see cref="ReleaseCredit"/>) — the number ticks
    ///    up WITH the chips. The director's payout beat (<see cref="RevealNow"/>) flushes whatever is left, so a push, a
    ///    loss, or a missing WinChipFly can never leave the number stuck behind.
    ///  • Outside a round-end (a claim, a chest, an IAP) credits roll immediately too.
    ///
    /// Display only — <see cref="WalletManager"/> stays the source of truth and this never writes to it. Replaces
    /// <see cref="BalanceHud"/> on the label you put it on (both would fight over the same text); leave BalanceHud on
    /// the other currencies.
    /// </summary>
    public sealed class ChipCountJuice : MonoBehaviour
    {
        [Header("Label")]
        [SerializeField] private TMP_Text label;
        [Tooltip("The table (auto-found; leave empty off-table). Used ONLY to re-arm the credit hold when a new round " +
                 "starts — without it just the first win would wait for the flying chips.")]
        [SerializeField] private TableController table;
        [Tooltip("Numeric format, e.g. \"#,0\" -> 1,234,567.")]
        [SerializeField] private string format = "#,0";

        [Header("Roll")]
        [Tooltip("Seconds for a FULL roll. Small changes finish sooner (the speed is scaled by how big the jump is), " +
                 "never quicker than Min Roll Seconds.")]
        [SerializeField] private float rollSeconds = 0.45f;
        [Tooltip("Floor on the roll time, so a tiny change still reads as a roll and not a snap.")]
        [SerializeField] private float minRollSeconds = 0.12f;
        [Tooltip("Scale the roll length to the TABLE's stakes rather than a fixed figure — what counts as a big win at " +
                 "a 1K-minimum table is noise at a 1M-minimum one. Off = always use Full Roll Fallback.")]
        [SerializeField] private bool scaleToTableStakes = true;
        [Tooltip("A win worth this many TABLE MINIMUMS earns the full Roll Seconds; less scales down toward the floor. " +
                 "Derived from the MINIMUM, not the maximum, because the minimum is what defines a table's tier and it " +
                 "still exists on no-limit / huge-limit tables where the maximum is 0.")]
        [SerializeField] private float fullRollMinBetMultiple = 20f;
        [Tooltip("Used when there is no table to read — the Home and Lobby balance HUDs — or when stake scaling is off.")]
        // long, NOT decimal: Unity's serializer does not support System.Decimal, so a decimal [SerializeField] never
        // appears in the Inspector and is permanently stuck at whatever the code says. Chips are whole numbers anyway.
        [SerializeField] private long fullRollFallback = 1000000;

        [Header("Punch")]
        [Tooltip("What scales. Defaults to the label's own RectTransform — point it at a PARENT (the chip icon + number " +
                 "together) if you want the whole pill to pop.")]
        [SerializeField] private RectTransform punchTarget;
        [SerializeField] private float punchScale = 1.3f;
        [SerializeField] private float punchSeconds = 0.2f;

        [Header("Floating amount (optional)")]
        [Tooltip("A TMP label that flies up and fades out, showing the change (\"+1,000\" / \"-500\"). Its AUTHORED " +
                 "position is where it ENDS — it starts Rise Distance BELOW that and rises into place. Leave empty to " +
                 "skip. Put it on its own object; this activates/deactivates it.")]
        [SerializeField] private TMP_Text floatingAmount;
        [Tooltip("How far BELOW the authored position it starts (anchored units).")]
        [SerializeField] private float floatRiseDistance = 60f;
        [Tooltip("Total time to rise and fade (seconds). Slow is the point — this is a readable receipt, not a flick.")]
        [SerializeField] private float floatSeconds = 1.1f;
        [Tooltip("Fade-IN time at the start (seconds).")]
        [SerializeField] private float floatFadeInSeconds = 0.12f;
        [Tooltip("Share of the total spent fading OUT at the end (0.6 = the last 60%).")]
        [Range(0.1f, 0.95f)]
        [SerializeField] private float floatFadeOutShare = 0.6f;
        [SerializeField] private bool floatOnGain = true;
        [SerializeField] private bool floatOnLoss = true;

        [Header("Colours — shared by the count flash AND the floating amount")]
        [Tooltip("Flash the COUNT label on a change. The two colours below apply to the floating amount either way.")]
        [SerializeField] private bool flashColour = true;
        [Tooltip("Gain colour: the count's flash and the floating \"+amount\". Overrides any gradient on the floater.")]
        [SerializeField] private Color gainColour = new Color(0.30f, 0.85f, 0.40f);
        [Tooltip("Loss colour: the count's flash and the floating \"-amount\". Overrides any gradient on the floater.")]
        [SerializeField] private Color lossColour = new Color(0.90f, 0.35f, 0.35f);
        [Tooltip("How long the flash takes to fade back to the label's normal colour.")]
        [SerializeField] private float flashSeconds = 0.35f;

        // Labels currently governed by a ChipCountJuice. BalanceHud / BalanceBinder check this and skip them, so no
        // other widget can snap a value onto a label that is mid-roll — which looked like "the balance jumps at settle
        // and the animation does nothing". A registry (not a same-object check) because BalanceBinder drives labels it
        // merely references, which a GetComponent test can never catch.
        private static readonly HashSet<TMP_Text> Governed = new HashSet<TMP_Text>();

        // Every LIVE instance. There should be exactly one per scene: RoundEndDirector and WinChipFly both resolve
        // theirs with FindAnyObjectByType, which returns an ARBITRARY match — so a stray second copy (e.g. left on the
        // floating-amount object) silently steals the director registration and the chip-landing feed, and the real
        // balance label then credits at settle instead of waiting for the chips. Cheap to detect, miserable to debug.
        private static readonly List<ChipCountJuice> Live = new List<ChipCountJuice>();

        /// <summary>True when a ChipCountJuice owns this label — other balance widgets must leave it alone.</summary>
        public static bool Owns(TMP_Text t) => t != null && Governed.Contains(t);

        private decimal _shown;          // what the label currently reads
        private decimal _target;         // where the wallet says it should be
        private bool _hasShown;

        private Coroutine _roll, _punch, _flash, _float;
        private RectTransform _floatRt;
        private Vector2 _floatEndPos;      // the AUTHORED position = where the floater finishes
        private Vector3 _baseScale = Vector3.one;
        private bool _hasBaseScale;
        private bool _warnedNoPunch;
        private int _flashDir;            // +1 flashing a gain, -1 a loss, 0 idle — stops same-direction restarts blinking
        private Color _baseColour = Color.white;

        // Round-end HOLD — same mechanism as SeatPlates / WinChipFly: while the director presents, credits wait.
        private MonoBehaviour _settleDirector;
        private bool _holdCredits;

        // An in-progress credit release (chips landing one by one): where the number was when the first chip hit, so
        // each slice can be measured against the whole win rather than the shrinking remainder.
        private decimal _creditFrom;
        private bool _creditActive;
        private bool _expectingBurst;   // WinChipFly owns the pending credit — RevealNow must not jump ahead of the chips

        private void Reset() => label = GetComponent<TMP_Text>();

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (label != null && floatingAmount == label)
                Debug.LogError($"[{nameof(ChipCountJuice)}] 'Label' and 'Floating Amount' are the SAME object " +
                    $"('{label.name}'). Label must be the MAIN BALANCE number; Floating Amount must be a SEPARATE " +
                    "label. Pointed at the same one, the balance is painted over the receipt — you see the whole " +
                    "balance counting where the '-3,000' should be.", this);
        }
#endif

        private void Awake()
        {
            if (label == null) label = GetComponent<TMP_Text>();

            // Same-object wiring produces exactly the "floater shows my balance and counts" symptom: Paint() writes the
            // rolling balance into the very label ShowFloating just set. Refuse the floater rather than fight over it.
            if (floatingAmount != null && floatingAmount == label)
            {
                Debug.LogError($"[{nameof(ChipCountJuice)}] 'Label' and 'Floating Amount' are the same object " +
                    $"('{label.name}') — ignoring Floating Amount. Assign Label = the main balance number and " +
                    "Floating Amount = a separate label.", this);
                floatingAmount = null;
            }
            if (punchTarget == null && label != null) punchTarget = label.rectTransform;
            if (label != null) _baseColour = label.color;
            // NOTE: _baseScale is captured LAZILY (see EnsureBaseScale), not here — HUD panels commonly animate in from
            // scale 0, and capturing 0 at Awake made every punch scale 0 → 0, i.e. no visible pop, permanently.

            if (floatingAmount != null)
            {
                _floatRt = floatingAmount.rectTransform;
                _floatEndPos = _floatRt.anchoredPosition;              // AUTHORED position = the end of the rise
                floatingAmount.gameObject.SetActive(false);             // shown only while a change is announcing
            }
        }

        // Resolve what to punch and its rest scale on FIRST use. Falls back to Vector3.one if the target is still
        // collapsed, so a punch can never be a no-op multiply by zero.
        private bool EnsureBaseScale()
        {
            if (punchTarget == null && label != null) punchTarget = label.rectTransform;
            if (punchTarget == null)
            {
                if (!_warnedNoPunch)
                {
                    _warnedNoPunch = true;
                    Debug.LogWarning($"[{nameof(ChipCountJuice)}] nothing to punch — assign Punch Target (or a Label " +
                                     "so its RectTransform can be used).", this);
                }
                return false;
            }
            if (_hasBaseScale) return true;

            Vector3 s = punchTarget.localScale;
            _baseScale = (s.x <= 0.0001f || s.y <= 0.0001f) ? Vector3.one : s;
            _hasBaseScale = true;
            return true;
        }

        private void OnEnable()
        {
            if (table == null) table = FindAnyObjectByType<TableController>(FindObjectsInactive.Include);
            if (table != null) table.OnBoardChanged += OnBoard;

            // Claim these labels so BalanceHud / BalanceBinder / SeatPlate leave them alone (see Governed).
            if (label != null) Governed.Add(label);
            if (floatingAmount != null) Governed.Add(floatingAmount);

            if (!Live.Contains(this)) Live.Add(this);
            WarnOnDuplicates();

            var wm = WalletManager.Instance;
            if (wm == null) return;
            wm.OnBalancesChanged += OnBalances;
            if (wm.Balances != null) SnapTo(wm.Chips);   // first paint is instant — no roll from zero on entering a scene
            _ = wm.RefreshAsync();
        }

        // Exactly one per scene, or the director / win-burst may bind to the wrong one (see the Live field).
        private static void WarnOnDuplicates()
        {
            if (Live.Count < 2) return;
            var names = new List<string>();
            for (int i = 0; i < Live.Count; i++)
                if (Live[i] != null)
                    names.Add($"'{Live[i].name}' (label='{(Live[i].label != null ? Live[i].label.name : "NONE")}')");
            Debug.LogError($"[{nameof(ChipCountJuice)}] {Live.Count} instances are active: {string.Join(", ", names)}. " +
                "There must be exactly ONE — RoundEndDirector and WinChipFly pick an arbitrary one, so a spare copy " +
                "steals the round-end hold and the chip-landing feed, and the real balance then credits at settle " +
                "instead of as the chips land. Remove the extra component (a copy left on the floating-amount object " +
                "is the usual culprit).");
        }

        private void OnDisable()
        {
            Live.Remove(this);
            if (label != null) Governed.Remove(label);
            if (floatingAmount != null) Governed.Remove(floatingAmount);
            if (table != null) table.OnBoardChanged -= OnBoard;
            if (WalletManager.Instance != null) WalletManager.Instance.OnBalancesChanged -= OnBalances;
        }

        // RE-ARM the credit hold for EVERY round. RevealNow drops the hold at the payout beat and nothing put it back,
        // so only the FIRST round's win waited for the flying chips — every later win ticked up (and flashed) at the
        // settle push instead, which reads as "the win colour happened once and never again".
        private void OnBoard(BoardSnapshot board)
        {
            if (board == null || !board.RoundInProgress) return;
            if (_settleDirector == null) return;          // no director presenting ⇒ nothing to wait for
            _holdCredits = true;
            _creditActive = false;
            _expectingBurst = false;
        }

        // ---- director hold (credits wait for the chips) ----

        /// <summary>The director arms deferral (at its OnEnable): credits wait for the payout beat / the flying chips.</summary>
        public void RegisterSettleDirector(MonoBehaviour director)
        {
            _settleDirector = director;
            _holdCredits = true;
        }

        public void UnregisterSettleDirector(MonoBehaviour director)
        {
            if (_settleDirector != director) return;
            _settleDirector = null;
            _holdCredits = false;
            if (_hasShown && _shown != _target) StartRoll(_target);   // don't strand the number if we're torn down mid-hold
        }

        /// <summary>
        /// <see cref="WinChipFly"/> claims the pending credit: it is about to burst and will feed the number in chip by
        /// chip, so <see cref="RevealNow"/> must not jump straight to the total in the meantime. Called synchronously
        /// when the burst is decided (before the chips are even spawned), because the director's payout beat fires in
        /// the same frame.
        /// </summary>
        public void ExpectCredit() => _expectingBurst = true;

        /// <summary>
        /// Director's payout beat: stop holding and roll to the real value. NO-OP while a burst owns the credit (the
        /// chips walk the number up instead) — otherwise this is what keeps a push, a loss, or a missing WinChipFly
        /// from leaving the number behind. Idempotent.
        /// </summary>
        public void RevealNow()
        {
            _holdCredits = false;
            if (_expectingBurst) return;   // the flying chips own the walk to the target
            _creditActive = false;
            if (!_hasShown || _shown == _target) return;
            ShowFloating(_target - _shown);
            StartRoll(_target);
        }

        /// <summary>
        /// One flying chip landed: advance the number to <paramref name="progress01"/> of the way through the pending
        /// credit and punch. Called by <see cref="WinChipFly"/> per chip, so the count ticks up WITH the chips instead
        /// of having already finished before they arrive.
        /// </summary>
        public void ReleaseCredit(float progress01)
        {
            if (!_hasShown) return;
            bool firstChip = !_creditActive;   // only the first landing flashes; the rest just tick + punch
            if (!_creditActive) { _creditFrom = _shown; _creditActive = true; }
            _holdCredits = false;   // the chips are arriving — credits may flow

            float p = Mathf.Clamp01(progress01);
            if (p >= 1f) _expectingBurst = false;                     // last chip — the burst no longer owns the credit

            decimal span = _target - _creditFrom;
            if (span <= 0m) return;                                   // nothing pending (already paid, or not a win)
            // Announce the WHOLE win on the first chip — one receipt for the round, not one per chip.
            if (firstChip) ShowFloating(span);
            decimal goal = _creditFrom + span * (decimal)p;
            StartRoll(goal, fast: true, flash: firstChip);
            Punch();
        }

        // ---- wallet → target ----

        private void OnBalances(WalletBalances b)
        {
            if (b == null) return;
            var wm = WalletManager.Instance;
            decimal next = wm != null ? wm.Chips : b.Chips;   // wm.Chips includes the optimistic prediction

            if (!_hasShown) { SnapTo(next); return; }

            // A change is only a transaction if there was a REAL balance before it. The baseline can legitimately be 0
            // (Balances exists but hasn't been fetched yet), and announcing from zero prints the whole wallet as the
            // amount won — so snap instead.
            if (_shown <= 0m) { SnapTo(next); return; }

            if (next == _target) return;

            _target = next;
            // A new value supersedes any in-flight slice walk — and drops a stale burst claim, so a burst torn down
            // mid-flight (scene change, force-finish) can't leave the number permanently behind.
            _creditActive = false;
            _expectingBurst = false;

            bool credit = _target > _shown;
            // Credits wait for the chips (see the class doc); deductions are the player's own action, so they roll now.
            if (credit && _holdCredits) return;

            ShowFloating(_target - _shown);   // the deduct receipt (and off-table credits: claims, chests, IAP)
            StartRoll(_target);
        }

        /// <summary>Paint a value with no animation (first paint, scene re-entry).</summary>
        public void SnapTo(decimal value)
        {
            StopRoll();
            _shown = _target = value;
            _hasShown = true;
            Paint();
        }

        // ---- roll + punch + flash ----

        /// <summary>
        /// The change that earns a FULL-length roll, in chips. Read from the live table so the same component feels
        /// right at every stake: a win is "big" relative to the table you're sitting at, not to a number baked into a
        /// prefab. Keyed off the table MINIMUM — it defines the tier, and unlike the maximum it is still meaningful on
        /// a no-limit table (where MaxBet is 0). Falls back to the fixed figure off-table (Home / Lobby HUDs).
        /// </summary>
        private decimal FullRollAmount()
        {
            if (!scaleToTableStakes) return fullRollFallback;

            var board = table != null ? table.Board : null;
            if (board == null) return fullRollFallback;

            if (board.MinBet > 0m) return board.MinBet * (decimal)Mathf.Max(1f, fullRollMinBetMultiple);
            if (board.MaxBet > 0m) return board.MaxBet;   // no minimum published — a max bet won is a fair yardstick
            return fullRollFallback;
        }

        private void StartRoll(decimal to, bool fast = false, bool flash = true)
        {
            StopRoll();
            if (!_hasShown) { SnapTo(to); return; }
            if (_shown == to) return;

            bool gain = to > _shown;
            // Scale the duration by how big the jump is, so a small tick doesn't take as long as a jackpot.
            decimal jump = to > _shown ? to - _shown : _shown - to;
            decimal reference = FullRollAmount();
            float f = reference > 0m ? (float)decimal.Divide(jump, reference) : 1f;
            float dur = Mathf.Max(minRollSeconds, Mathf.Min(rollSeconds, rollSeconds * Mathf.Clamp01(f)));
            if (fast) dur = Mathf.Max(minRollSeconds, dur * 0.6f);   // chip-by-chip ticks are meant to feel snappy

            _roll = StartCoroutine(RollRoutine(to, dur));
            if (!fast) Punch();                                       // slice releases punch on their own cadence
            if (flash) Flash(gain);
        }

        private void StopRoll()
        {
            if (_roll != null) { StopCoroutine(_roll); _roll = null; }
        }

        private IEnumerator RollRoutine(decimal to, float duration)
        {
            decimal from = _shown;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float u = Mathf.Clamp01(t / duration);
                // Ease-out: fast off the mark, settling into the final figure — reads as a counter spinning down.
                float e = 1f - (1f - u) * (1f - u);
                _shown = from + (to - from) * (decimal)e;
                Paint();
                yield return null;
            }
            _shown = to;
            Paint();
            _roll = null;
        }

        private void Paint()
        {
            if (label != null) label.text = decimal.Round(_shown, 0, System.MidpointRounding.AwayFromZero).ToString(format);
        }

        /// <summary>Scale-punch the label (public so a chip landing can pop it directly).</summary>
        public void Punch()
        {
            if (punchSeconds <= 0f || !EnsureBaseScale()) return;
            if (_punch != null) StopCoroutine(_punch);
            _punch = StartCoroutine(PunchRoutine());
        }

        private IEnumerator PunchRoutine()
        {
            Vector3 up = _baseScale * punchScale;
            float upT = punchSeconds * 0.35f, dnT = punchSeconds * 0.65f, t = 0f;
            while (t < upT) { t += Time.unscaledDeltaTime; punchTarget.localScale = Vector3.Lerp(_baseScale, up, t / upT); yield return null; }
            t = 0f;
            while (t < dnT) { t += Time.unscaledDeltaTime; punchTarget.localScale = Vector3.Lerp(up, _baseScale, t / dnT); yield return null; }
            punchTarget.localScale = _baseScale;
            _punch = null;
        }

        /// <summary>
        /// Announce a change as a floating amount: it appears <c>Rise Distance</c> BELOW its authored spot and drifts up
        /// into it, fading out as it arrives. One per money event — a win shows the WHOLE win when the first chip lands,
        /// not a twelfth of it per chip.
        /// </summary>
        private void ShowFloating(decimal delta)
        {
            if (floatingAmount == null || _floatRt == null || delta == 0m) return;

            // GUARD (one place, because there are three callers): every delta is `target - baseline`, so the baseline
            // is recoverable as `target - delta`. If that baseline is 0 the "change" is really a FIRST PAINT — the
            // wallet arriving on a label that was showing nothing — and announcing it printed the player's whole
            // balance as the amount won. A real transaction always has a real balance before it.
            decimal baseline = _target - delta;
            if (baseline <= 0m) return;

            bool gain = delta > 0m;
            if (gain && !floatOnGain) return;
            if (!gain && !floatOnLoss) return;

            // ALWAYS signed. Format the magnitude and prepend the sign ourselves rather than relying on the format
            // string to emit a minus — "#,0" happens to, but a format with a negative section (e.g. "#,0;#,0") would
            // silently drop it, and a gain never gets a "+" from any format.
            SetFloatingText(delta);
            if (_float != null) StopCoroutine(_float);
            _float = StartCoroutine(FloatRoutine(gain ? gainColour : lossColour));
        }

        private void SetFloatingText(decimal delta)
        {
            // ALWAYS signed. Format the magnitude and prepend the sign ourselves rather than relying on the format
            // string to emit a minus — "#,0" happens to, but a format with a negative section (e.g. "#,0;#,0") would
            // silently drop it, and a gain never gets a "+" from any format.
            bool gain = delta > 0m;
            decimal magnitude = gain ? delta : -delta;
            floatingAmount.text = (gain ? "+" : "-") + magnitude.ToString(format);
        }

        private IEnumerator FloatRoutine(Color colour)
        {
            floatingAmount.gameObject.SetActive(true);

            // The floater's colour comes from Gain Colour / Loss Colour (the same pair the count flash uses), so the
            // receipt and the number always agree. A TMP VERTEX GRADIENT on the label would win over `.color` and the
            // tint would silently never show, so switch it off — whatever gradient is authored on the object is
            // deliberately overridden here.
            floatingAmount.enableVertexGradient = false;

            Vector2 from = _floatEndPos + Vector2.down * floatRiseDistance;
            float duration = Mathf.Max(0.05f, floatSeconds);
            float fadeIn = Mathf.Clamp(floatFadeInSeconds, 0f, duration * 0.5f);
            float outStart = duration * (1f - floatFadeOutShare);

            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float u = Mathf.Clamp01(t / duration);

                // Ease-out rise: it lifts away from the balance and settles into the authored spot as it fades.
                float e = 1f - (1f - u) * (1f - u);
                _floatRt.anchoredPosition = Vector2.LerpUnclamped(from, _floatEndPos, e);

                float a = t < fadeIn && fadeIn > 0f
                    ? t / fadeIn                                              // fade in
                    : (t < outStart ? 1f : 1f - Mathf.Clamp01((t - outStart) / Mathf.Max(0.0001f, duration - outStart)));
                floatingAmount.color = new Color(colour.r, colour.g, colour.b, a);
                yield return null;
            }

            floatingAmount.gameObject.SetActive(false);
            _floatRt.anchoredPosition = _floatEndPos;   // leave it parked at the authored spot for the next show
            _float = null;
        }

        private void Flash(bool gain)
        {
            if (!flashColour || label == null || flashSeconds <= 0f) return;
            int dir = gain ? 1 : -1;

            // Already fading in this direction? LET IT FINISH. Restarting was the blink: several rolls landing in quick
            // succession (a credit arriving chip by chip, or a prediction followed by the server's value) each snapped
            // the colour back to full strength instead of one smooth fade.
            if (_flash != null && _flashDir == dir) return;

            if (_flash != null) StopCoroutine(_flash);
            _flashDir = dir;
            _flash = StartCoroutine(FlashRoutine(gain ? gainColour : lossColour));
        }

        private IEnumerator FlashRoutine(Color from)
        {
            float t = 0f;
            while (t < flashSeconds)
            {
                t += Time.unscaledDeltaTime;
                label.color = Color.Lerp(from, _baseColour, Mathf.Clamp01(t / flashSeconds));
                yield return null;
            }
            label.color = _baseColour;
            _flash = null;
            _flashDir = 0;
        }
    }
}
