using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Slow "breathing" pulse for a UI element — eases its CanvasGroup alpha (and optionally a gentle scale) up and
    /// down on a sine while the object is active, so a bet-spot highlight feels alive instead of static. Put it on
    /// the highlighter; it only animates while the GameObject is enabled, so toggling the object on/off cleanly
    /// starts/stops the effect. Runs on unscaled time, so it keeps pulsing even if the game is paused.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class UiPulse : MonoBehaviour
    {
        [SerializeField] private CanvasGroup group;
        [Tooltip("Alpha at the dim end of the pulse.")]
        [SerializeField, Range(0f, 1f)] private float minAlpha = 0.35f;
        [Tooltip("Alpha at the bright end of the pulse.")]
        [SerializeField, Range(0f, 1f)] private float maxAlpha = 1f;
        [Tooltip("Seconds for one full dim → bright → dim cycle. Bigger = slower breathing.")]
        [SerializeField] private float period = 1.4f;
        [Tooltip("Optional gentle scale pulse amplitude (0 = alpha only; e.g. 0.04 = ±4%).")]
        [SerializeField] private float scalePulse = 0f;

        private Vector3 _baseScale = Vector3.one;
        private float _t;

        private void Awake()
        {
            if (group == null) group = GetComponent<CanvasGroup>();
            _baseScale = transform.localScale;
        }

        private void OnEnable() => _t = 0f;   // start from a consistent phase each time it appears

        private void Update()
        {
            if (group == null || period <= 0f) return;
            _t += Time.unscaledDeltaTime;
            float s = (Mathf.Sin(_t / period * (Mathf.PI * 2f)) + 1f) * 0.5f;   // 0..1
            group.alpha = Mathf.Lerp(minAlpha, maxAlpha, s);
            if (scalePulse > 0f) transform.localScale = _baseScale * (1f + scalePulse * (s * 2f - 1f));
        }

        private void OnDisable()
        {
            transform.localScale = _baseScale;   // leave it clean for next time it's shown
        }
    }
}
