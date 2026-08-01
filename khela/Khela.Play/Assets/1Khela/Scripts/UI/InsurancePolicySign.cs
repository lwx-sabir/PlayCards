using System.Collections;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The house-rule sign on the felt — "Insurance pays 2:1" — shown only while the BETTING WINDOW is open. It UNROLLS
    /// from top to bottom when the window opens, its background then pulses gently, and it rolls back up when the round
    /// starts. (The rule it advertises is real: insurance returns 3x the stake, i.e. 2:1 plus the stake back — see
    /// BlackjackSettlement.InsuranceWinMultiplier.)
    ///
    /// Named for its first use, but nothing in here is insurance-specific — the behaviour is purely "show while betting
    /// is open". Point a second instance at another visual for any between-rounds sign (table rules, a promo).
    ///
    /// AUTHORING: the sign is a world-space UI object on the table canvas, authored at its FINAL size and left
    /// DISABLED — this activates it when the window opens. The unroll is a Y-scale, so set the visual's
    /// RectTransform PIVOT to the TOP (Y = 1) or it will open from its middle outwards instead of downwards.
    ///
    /// IMPORTANT (the recurring disabled-watcher trap): put this component on an ALWAYS-ACTIVE object (e.g. the table
    /// root or TableHUD) and assign <see cref="panel"/> = the sign. A component sitting on the object it hides gets no
    /// Update once hidden, so it could never show itself again.
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
        [Tooltip("The sign VISUAL — a SEPARATE object from this controller, authored at full size and disabled by " +
                 "default. Set its pivot Y to 1 (top) so the unroll opens downward.")]
        [SerializeField] private RectTransform panel;

        [Header("Unroll")]
        [Tooltip("Seconds for the sign to open from top to bottom.")]
        [SerializeField] private float openSeconds = 0.65f;
        [Tooltip("Seconds to roll back up when the window closes. 0 = disappear instantly.")]
        [SerializeField] private float closeSeconds = 0.25f;
        [Tooltip("Juice: overshoot on the unroll — 0 = a clean open, ~1.7 = stretches past full then settles.")]
        [SerializeField] private float overshoot = 0.6f;

        [Header("Background pulse")]
        [Tooltip("The background Image that breathes once the sign is open. Leave empty for no pulse.")]
        [SerializeField] private Graphic blinkGraphic;
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

        private bool _shown;
        private bool _windowShown;      // latched for the CURRENT window: a late tween must not re-trigger the hold
        private Vector3 _baseScale = Vector3.one;
        private Coroutine _roll;

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (panel != null && panel.gameObject == gameObject)
                Debug.LogError($"[{nameof(InsurancePolicySign)}] is on the SAME GameObject as its 'panel'. Once hidden it " +
                    "stops receiving Update, so the sign could never re-appear. Move this to an ALWAYS-ACTIVE object " +
                    "and set 'panel' to the sign visual.", this);
        }
#endif

        private void Awake()
        {
            if (panel == null) return;
            // Authored scale = full size. Guard a collapsed authoring value, or every unroll would scale 0 → 0.
            Vector3 s = panel.localScale;
            _baseScale = (s.x <= 0.0001f || s.y <= 0.0001f) ? Vector3.one : s;
            if (panel.gameObject != gameObject) panel.gameObject.SetActive(false);   // disabled by default
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
            if (_shown) Pulse();
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
                if (_shown) Hide();
                return;
            }

            // Hold only the FIRST appearance: RoundEndSettling can re-latch on a stray late card tween, which would
            // otherwise blink the sign away after it had already opened.
            if (!_windowShown && holdForRoundEnd && view != null && view.RoundEndSettling) return;

            _windowShown = true;
            if (!_shown) Show();
        }

        private void Show()
        {
            if (panel == null) return;
            _shown = true;
            if (panel.gameObject != gameObject && !panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
            panel.localScale = new Vector3(_baseScale.x, 0f, _baseScale.z);   // closed, then unroll downward
            StartRoll(open: true);
        }

        private void Hide()
        {
            if (panel == null) return;
            _shown = false;
            RestoreAlpha();
            StartRoll(open: false);
        }

        private void StartRoll(bool open)
        {
            if (_roll != null) StopCoroutine(_roll);
            _roll = StartCoroutine(RollRoutine(open));
        }

        private IEnumerator RollRoutine(bool open)
        {
            float duration = open ? openSeconds : closeSeconds;
            float from = panel.localScale.y;
            float to = open ? _baseScale.y : 0f;

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
                    panel.localScale = new Vector3(_baseScale.x, Mathf.LerpUnclamped(from, to, k), _baseScale.z);
                    yield return null;
                }
            }

            panel.localScale = new Vector3(_baseScale.x, to, _baseScale.z);
            if (!open && panel.gameObject != gameObject) panel.gameObject.SetActive(false);
            _roll = null;
        }

        // Smooth breathe on the background — a sine so there are no hard edges at either end of the cycle.
        private void Pulse()
        {
            if (blinkGraphic == null || blinkPeriod <= 0f) return;
            float u = (Mathf.Sin(Time.unscaledTime * (2f * Mathf.PI / blinkPeriod)) + 1f) * 0.5f;
            var c = blinkGraphic.color;
            c.a = Mathf.Lerp(blinkMinAlpha, blinkMaxAlpha, u);
            blinkGraphic.color = c;
        }

        private void RestoreAlpha()
        {
            if (blinkGraphic == null) return;
            var c = blinkGraphic.color;
            c.a = blinkMaxAlpha;
            blinkGraphic.color = c;
        }
    }
}
