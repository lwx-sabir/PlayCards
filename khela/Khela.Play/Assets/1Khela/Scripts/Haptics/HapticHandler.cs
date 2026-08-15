using UnityEngine;

namespace PlayCard.Haptics
{
    /// <summary>
    /// A serializable, inspector-authored haptic you can drop onto a component as a field and fire with
    /// <see cref="Play"/> — so designers tune feel per object without touching code. Supports an optional
    /// per-handler cooldown and a dynamic-intensity ramp (repeated triggers escalate, then reset after idle),
    /// e.g. an escalating combo or a charge-up.
    ///
    /// Self-contained: uses only Unity built-ins (the source relied on Watermelon's DuoFloat/[Slider]/[ShowIf]).
    /// </summary>
    [System.Serializable]
    public class HapticHandler
    {
        [SerializeField] private float duration = 0.05f;
        [Range(0f, 1f)][SerializeField] private float intensity = 0.5f;
        [Range(0f, 1f)][SerializeField] private float sharpness = 0.5f;

        [Header("Advanced")]
        [Tooltip("Minimum seconds between plays of THIS handler (on top of the global Haptic.MinInterval). 0 = none.")]
        [SerializeField] private float minDelay;

        [Tooltip("If set, repeated plays ramp intensity up through the range, resetting after the reset time of idle.")]
        [SerializeField] private bool dynamicIntensity;
        [SerializeField] private Vector2 intensityRange = new Vector2(0.2f, 1.0f);
        [Min(1)][SerializeField] private int intensitySteps = 10;
        [Min(0f)][SerializeField] private float intensityResetTime = 1.0f;

        private float _lastPlayedTime = float.MinValue;
        private int _currentStep;
        private float _resetAt;

        public void Play()
        {
            float now = Time.unscaledTime;
            if (minDelay > 0f && now < _lastPlayedTime + minDelay) return;
            _lastPlayedTime = now;

            if (dynamicIntensity)
            {
                if (now > _resetAt) _currentStep = 0;          // idled long enough → ramp restarts
                _resetAt = now + intensityResetTime;

                float t = intensitySteps > 0 ? Mathf.Clamp01((float)_currentStep / intensitySteps) : 1f;
                float rampedIntensity = Mathf.Lerp(intensityRange.x, intensityRange.y, t);
                _currentStep = Mathf.Min(_currentStep + 1, intensitySteps);

                Haptic.Play(duration, rampedIntensity, sharpness);
            }
            else
            {
                Haptic.Play(duration, intensity, sharpness);
            }
        }
    }
}
