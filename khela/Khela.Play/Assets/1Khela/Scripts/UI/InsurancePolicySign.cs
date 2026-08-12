using System.Collections;
using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The house-rule signs on the felt — "Insurance pays 2:1", "Dealer stands on soft 17" — shown only while the
    /// BETTING WINDOW is open. A sign UNROLLS from top to bottom, its background pulses gently while it sits there,
    /// and it rolls back up when the window closes. (The insurance rule it advertises is real: insurance returns 3x
    /// the stake, i.e. 2:1 plus the stake back — see BlackjackSettlement.InsuranceWinMultiplier.)
    ///
    /// With MORE THAN ONE sign assigned it ROTATES them: exactly one is ever on screen, each holds for a random dwell
    /// between <see cref="minHoldSeconds"/> and <see cref="maxHoldSeconds"/>, rolls up, and the next rolls down. With a
    /// single sign it just opens and stays, which is the original behaviour.
    ///
    /// The rotation position CARRIES OVER between betting windows. Restarting at the first sign every window would
    /// mean a later sign is only ever seen when a window runs long, and on a busy table it might never show at all.
    ///
    /// AUTHORING: each sign is a world-space UI object on the table canvas, authored at its FINAL size and left
    /// DISABLED — this activates them. The unroll is a Y-scale, so set each visual's RectTransform PIVOT to the TOP
    /// (Y = 1) or it will open from its middle outwards instead of downwards.
    ///
    /// IMPORTANT (the recurring disabled-watcher trap): put this component on an ALWAYS-ACTIVE object (e.g. the table
    /// root or TableHUD), never on a sign it hides. A component sitting on the object it hides gets no Update once
    /// hidden, so it could never show anything again.
    /// </summary>
    public sealed class InsurancePolicySign : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Auto-found if empty.")]
        [SerializeField] private TableController table;
        [Tooltip("The table view — used to hold the sign back until the round-end ceremony has finished, so it doesn't " +
                 "unroll over the payout. Optional (auto-found).")]
        [SerializeField] private BlackjackTableView view;

        [Header("Sign")]
        [Tooltip("The FIRST sign visual — a SEPARATE object from this controller, authored at full size and disabled " +
                 "by default. Set its pivot Y to 1 (top) so the unroll opens downward.")]
        [SerializeField] private RectTransform panel;
        [Tooltip("The background Image that breathes while THIS sign is open. Leave empty for no pulse.")]
        [SerializeField] private Graphic blinkGraphic;

        [Header("More signs (rotated with the one above)")]
        [Tooltip("Extra signs shown in turn with the first — e.g. Table_label_Dealer_Stand. Each gets its own " +
                 "background graphic for the pulse. Leave empty for a single, always-shown sign.")]
        [SerializeField] private ExtraSign[] extraSigns = new ExtraSign[0];

        [System.Serializable]
        public struct ExtraSign
        {
            [Tooltip("The sign visual. Same authoring rules as the first: full size, disabled, pivot Y = 1.")]
            public RectTransform panel;
            [Tooltip("Its background Image, for the pulse. Optional.")]
            public Graphic blinkGraphic;
        }

        [Header("Rotation")]
        [Tooltip("Shortest a sign stays up before rolling away, in seconds. Only used when 2+ signs are assigned.")]
        [SerializeField] private float minHoldSeconds = 5f;
        [Tooltip("Longest a sign stays up. A random value in [min, max] is picked for EACH showing, so the table " +
                 "doesn't feel metronomic.")]
        [SerializeField] private float maxHoldSeconds = 7f;

        [Header("Unroll")]
        [Tooltip("Seconds for the sign to open from top to bottom.")]
        [SerializeField] private float openSeconds = 0.65f;
        [Tooltip("Seconds to roll back up when the window closes. 0 = disappear instantly.")]
        [SerializeField] private float closeSeconds = 0.25f;
        [Tooltip("Juice: overshoot on the unroll — 0 = a clean open, ~1.7 = stretches past full then settles.")]
        [SerializeField] private float overshoot = 0.6f;

        [Header("Background pulse")]
        [Tooltip("Seconds for one full dim→bright→dim cycle.")]
        [SerializeField] private float blinkPeriod = 1.4f;
        [Range(0f, 1f)]
        [SerializeField] private float blinkMinAlpha = 0.55f;
        [Range(0f, 1f)]
        [SerializeField] private float blinkMaxAlpha = 1f;

        [Header("When to show")]
        [Tooltip("Only show it to a SEATED player. Off = spectators see the sign too.")]
        [SerializeField] private bool requireSeated = false;
        [Tooltip("Hold the first appearance until the round-end ceremony (reveal → collect → pay → sweep) has finished, " +
                 "so the sign doesn't unroll on top of the payout.")]
        [SerializeField] private bool holdForRoundEnd = true;

        // One entry per assigned sign, in rotation order. Base scale is captured at Awake because the unroll writes
        // localScale.y directly — reading it later would pick up a mid-tween value as "full size".
        private sealed class Sign
        {
            public RectTransform Panel;
            public Graphic Blink;
            public Vector3 BaseScale = Vector3.one;
        }

        private readonly List<Sign> _signs = new List<Sign>();

        private bool _windowShown;   // latched for the CURRENT window: a late tween must not re-trigger the hold
        private int _index;          // whose turn it is — deliberately NOT reset between windows
        private int _visible = -1;   // index currently on screen, -1 = none
        private Coroutine _cycle;
        private Coroutine _closing;  // the detached roll-up started when a window closes

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (panel != null && panel.gameObject == gameObject)
                Debug.LogError($"[{nameof(InsurancePolicySign)}] is on the SAME GameObject as its 'panel'. Once hidden it " +
                    "stops receiving Update, so the sign could never re-appear. Move this to an ALWAYS-ACTIVE object " +
                    "and set 'panel' to the sign visual.", this);

            if (maxHoldSeconds < minHoldSeconds) maxHoldSeconds = minHoldSeconds;
        }
#endif

        private void Awake()
        {
            AddSign(panel, blinkGraphic);
            if (extraSigns != null)
                foreach (var e in extraSigns) AddSign(e.panel, e.blinkGraphic);
        }

        private void AddSign(RectTransform rect, Graphic blink)
        {
            if (rect == null) return;

            // Authored scale = full size. Guard a collapsed authoring value, or every unroll would scale 0 → 0.
            Vector3 s = rect.localScale;
            var sign = new Sign
            {
                Panel = rect,
                Blink = blink,
                BaseScale = (s.x <= 0.0001f || s.y <= 0.0001f) ? Vector3.one : s,
            };
            _signs.Add(sign);

            if (rect.gameObject != gameObject) rect.gameObject.SetActive(false);   // disabled by default
        }

        private void OnEnable()
        {
            if (table == null) table = FindAnyObjectByType<TableController>(FindObjectsInactive.Include);
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>(FindObjectsInactive.Include);
        }

        private void Update()
        {
            // The window opens and closes BETWEEN board pushes (the round-end ceremony finishes locally with no server
            // message), so this can't be board-driven alone — same reason BetTimerPopup polls.
            Apply(table != null ? table.Board : null);
            Pulse();
        }

        private void Apply(BoardSnapshot board)
        {
            bool windowOpen = board != null
                              && !board.RoundInProgress
                              && board.BettingExpiresAt.HasValue
                              && (!requireSeated || (table != null && table.MySeat > 0));

            if (!windowOpen)
            {
                _windowShown = false;      // window closed — re-arm for the next one
                StopCycle();
                return;
            }

            // Hold only the FIRST appearance: RoundEndSettling can re-latch on a stray late card tween, which would
            // otherwise blink the sign away after it had already opened.
            if (!_windowShown && holdForRoundEnd && view != null && view.RoundEndSettling) return;

            _windowShown = true;
            if (_cycle != null || _signs.Count == 0) return;

            // A window can re-open while the last one's sign is still rolling away. Kill that tween first, or it and
            // the fresh unroll both write localScale.y on the same rect and the sign jitters or ends up half-open.
            if (_closing != null) { StopCoroutine(_closing); _closing = null; }

            _cycle = StartCoroutine(CycleRoutine());
        }

        private void StopCycle()
        {
            if (_cycle != null) { StopCoroutine(_cycle); _cycle = null; }
            if (_visible >= 0)
            {
                var sign = _signs[_visible];
                RestoreAlpha(sign);
                // Roll it away rather than snapping it off, so closing looks the same however the window ended.
                // Detached (the cycle that owned it has just been stopped), so it's tracked to be cancellable.
                _closing = StartCoroutine(RollRoutine(sign, open: false));
                _visible = -1;
            }
        }

        /// <summary>
        /// Show each sign in turn for a random dwell. A single sign opens once and stays (no pointless roll-up/roll-down
        /// of the only thing there is to look at); two or more alternate for as long as the window is open.
        /// </summary>
        private IEnumerator CycleRoutine()
        {
            while (true)
            {
                if (_index >= _signs.Count) _index = 0;
                var sign = _signs[_index];

                _visible = _index;
                yield return RollRoutine(sign, open: true);

                if (_signs.Count <= 1) yield break;   // nothing to rotate to — leave it up until the window closes

                // Random per SHOWING, not per sign, so the rhythm doesn't settle into a pattern.
                float hold = Random.Range(minHoldSeconds, Mathf.Max(minHoldSeconds, maxHoldSeconds));
                float t = 0f;
                while (t < hold) { t += Time.unscaledDeltaTime; yield return null; }

                RestoreAlpha(sign);
                yield return RollRoutine(sign, open: false);
                _visible = -1;

                _index = (_index + 1) % _signs.Count;
            }
        }

        private IEnumerator RollRoutine(Sign sign, bool open)
        {
            var rect = sign.Panel;
            if (rect == null) yield break;

            if (open && rect.gameObject != gameObject && !rect.gameObject.activeSelf)
            {
                rect.gameObject.SetActive(true);
                rect.localScale = new Vector3(sign.BaseScale.x, 0f, sign.BaseScale.z);   // closed, then unroll downward
            }

            float duration = open ? openSeconds : closeSeconds;
            float from = rect.localScale.y;
            float to = open ? sign.BaseScale.y : 0f;

            if (duration > 0f)
            {
                float t = 0f;
                while (t < duration)
                {
                    t += Time.unscaledDeltaTime;
                    float raw = Mathf.Clamp01(t / duration);
                    // Overshoot only on the way OPEN — a sign that springs past its size as it drops reads as unrolling;
                    // on the way out a plain ease keeps it from flicking oddly. LerpUnclamped lets it pass the endpoint.
                    float k = open ? UITween.EaseOutBack(raw, overshoot) : raw * raw;
                    rect.localScale = new Vector3(sign.BaseScale.x, Mathf.LerpUnclamped(from, to, k), sign.BaseScale.z);
                    yield return null;
                }
            }

            rect.localScale = new Vector3(sign.BaseScale.x, to, sign.BaseScale.z);
            if (!open && rect.gameObject != gameObject) rect.gameObject.SetActive(false);
        }

        // Smooth breathe on the background of whichever sign is up — a sine so there are no hard edges at either end.
        private void Pulse()
        {
            if (_visible < 0 || _visible >= _signs.Count) return;
            var blink = _signs[_visible].Blink;
            if (blink == null || blinkPeriod <= 0f) return;

            float u = (Mathf.Sin(Time.unscaledTime * (2f * Mathf.PI / blinkPeriod)) + 1f) * 0.5f;
            var c = blink.color;
            c.a = Mathf.Lerp(blinkMinAlpha, blinkMaxAlpha, u);
            blink.color = c;
        }

        private void RestoreAlpha(Sign sign)
        {
            if (sign?.Blink == null) return;
            var c = sign.Blink.color;
            c.a = blinkMaxAlpha;
            sign.Blink.color = c;
        }
    }
}
