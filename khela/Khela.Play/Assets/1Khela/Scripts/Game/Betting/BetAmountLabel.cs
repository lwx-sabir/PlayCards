using System.Collections;
using PlayCard.UI;   // UITween
using TMPro;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// One seat's "amount on the felt" badge — the pill that reads e.g. "1M" under a bet spot. Driven by
    /// <see cref="BetStacks"/>: it appears with the first chip dropped, tracks the running total, and rolls away when
    /// the spot is empty.
    ///
    /// It owns the WHOLE badge, not just the text. Clearing a TMP_Text would leave the pill's background sitting on
    /// the felt as an empty shape, so show/hide scales THIS object on its X axis: it opens LEFT → RIGHT and closes
    /// RIGHT → LEFT, with an elastic overshoot.
    ///
    /// AUTHORING — both matter:
    ///  • Put this component on the PARENT that contains the background AND the text. The tween scales that object,
    ///    so everything under it hides together; anything outside it stays on screen (a text left as a sibling of
    ///    the background is exactly how the number ends up floating on the felt with nothing behind it).
    ///  • Set that RectTransform's PIVOT X to 0. The open is a horizontal scale, so a centred pivot makes it grow
    ///    from the middle outwards instead of unrolling from its left edge.
    /// </summary>
    public sealed class BetAmountLabel : MonoBehaviour
    {
        [Tooltip("The amount text. Auto-found in children if left empty.")]
        [SerializeField] private TMP_Text label;

        [Header("Tween")]
        [Tooltip("Seconds to unroll open (left → right).")]
        [SerializeField] private float openSeconds = 0.28f;
        [Tooltip("Seconds to roll closed (right → left).")]
        [SerializeField] private float closeSeconds = 0.18f;
        [Tooltip("Elasticity: how far it springs past full width before settling. 0 = a clean open.")]
        [SerializeField] private float overshoot = 1.6f;

        // ALWAYS this object's own RectTransform — never a sub-object. It has to be the common parent of the
        // background AND the text, or scaling it hides only part of the badge (pointing this at the background image
        // left the number floating on the felt with nothing behind it).
        private RectTransform badge;

        private Vector3 _baseScale = Vector3.one;
        private bool _hasBaseScale;
        private bool _shown;
        private Coroutine _tween;

        private void Awake()
        {
            if (label == null) label = GetComponentInChildren<TMP_Text>(true);
            badge = transform as RectTransform;
            CaptureBaseScale();
            // Closed by default — nothing has been bet yet. Collapse rather than deactivate so the first Show can
            // tween from a live object (and so any layout around it is already resolved).
            if (badge != null) badge.localScale = new Vector3(0f, _baseScale.y, _baseScale.z);
            _shown = false;
        }

        // Captured lazily-safe: a badge authored at scale 0 (or inside a panel that animates in) would otherwise
        // pin every open to 0 → 0, the same trap that made the chip-count punch invisible.
        private void CaptureBaseScale()
        {
            if (_hasBaseScale || badge == null) return;
            Vector3 s = badge.localScale;
            _baseScale = (s.x <= 0.0001f || s.y <= 0.0001f) ? Vector3.one : s;
            _hasBaseScale = true;
        }

        /// <summary>Show the badge with this text. If it's already open, only the text changes — the open tween does
        /// NOT replay, so it doesn't re-animate on every chip dropped.</summary>
        public void Show(string text)
        {
            CaptureBaseScale();
            if (label != null) label.text = text;
            if (_shown) return;

            _shown = true;
            StartTween(open: true);
        }

        /// <summary>Roll the badge away (right → left). No-op if already closed.</summary>
        public void Hide()
        {
            if (!_shown) return;
            _shown = false;
            StartTween(open: false);
        }

        private void StartTween(bool open)
        {
            if (badge == null) return;
            if (_tween != null) StopCoroutine(_tween);
            if (!isActiveAndEnabled)
            {
                // Can't run a coroutine while inactive — snap so the badge can never be left half-open.
                badge.localScale = new Vector3(open ? _baseScale.x : 0f, _baseScale.y, _baseScale.z);
                return;
            }
            _tween = StartCoroutine(ScaleRoutine(open));
        }

        private IEnumerator ScaleRoutine(bool open)
        {
            float from = badge.localScale.x;
            float to = open ? _baseScale.x : 0f;
            float duration = open ? openSeconds : closeSeconds;

            float t = 0f;
            while (t < duration && duration > 0f)
            {
                t += Time.unscaledDeltaTime;
                float raw = Mathf.Clamp01(t / duration);
                // Springs past full on the way OPEN, winds back in on the way CLOSED. LerpUnclamped so the overshoot
                // can actually exceed the endpoint.
                float k = open ? UITween.EaseOutBack(raw, overshoot) : UITween.EaseInBack(raw, overshoot);
                badge.localScale = new Vector3(Mathf.LerpUnclamped(from, to, k), _baseScale.y, _baseScale.z);
                yield return null;
            }

            badge.localScale = new Vector3(to, _baseScale.y, _baseScale.z);
            _tween = null;
        }
    }
}
