using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Haptics
{
    /// <summary>
    /// Android wrapper over the OS <c>Vibrator</c> via JNI. On API 26+ it uses <c>VibrationEffect.createOneShot</c>
    /// (amplitude 1..255 from intensity) and <c>createWaveform</c> for patterns; below 26 it falls back to the
    /// legacy pattern <c>vibrate</c>. Unlike the source system it checks BOTH <c>hasVibrator()</c> and
    /// <c>hasAmplitudeControl()</c>: on a device without amplitude control it plays at the OS default amplitude
    /// (still felt, just not intensity-scaled) rather than silently doing nothing meaningful.
    /// </summary>
    public sealed class AndroidHapticWrapper : BaseHapticWrapper
    {
        // VibrationEffect.DEFAULT_AMPLITUDE — "let the device choose" (used when amplitude control is unavailable).
        private const int DefaultAmplitude = -1;

        private AndroidJavaObject _vibrator;
        private AndroidJavaClass _effectClass;
        private int _sdk;
        private bool _hasVibrator;
        private bool _hasAmplitudeControl;
        private Dictionary<int, AndroidPattern> _patterns;

        public override void Init()
        {
            _patterns = new Dictionary<int, AndroidPattern>();
#if UNITY_ANDROID && !UNITY_EDITOR
            Try(() =>
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    _vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    _sdk = version.GetStatic<int>("SDK_INT");
                }

                _hasVibrator = _vibrator != null && _vibrator.Call<bool>("hasVibrator");
                _hasAmplitudeControl = _hasVibrator && _sdk >= 26 && _vibrator.Call<bool>("hasAmplitudeControl");
                if (_sdk >= 26) _effectClass = new AndroidJavaClass("android.os.VibrationEffect");

                Log($"Android init — sdk {_sdk}, hasVibrator {_hasVibrator}, amplitudeControl {_hasAmplitudeControl}");
            }, "Android init failed");
#endif
        }

        public override void Play(float duration, float intensity, float sharpness) // sharpness unused on Android
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_hasVibrator) return;
            Try(() =>
            {
                long ms = (long)(duration * 1000f);
                if (ms <= 0) ms = 1;

                if (_sdk >= 26)
                {
                    int amplitude = _hasAmplitudeControl
                        ? Mathf.Clamp((int)Mathf.Lerp(1, 255, intensity), 1, 255)
                        : DefaultAmplitude;
                    using (var effect = _effectClass.CallStatic<AndroidJavaObject>("createOneShot", ms, amplitude))
                        _vibrator.Call("vibrate", effect);
                }
                else
                {
                    _vibrator.Call("vibrate", ms);
                }
            }, "Android play failed");
#endif
        }

        public override void Play(string patternId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_hasVibrator || _patterns == null) return;
            if (!_patterns.TryGetValue(patternId.GetHashCode(), out var p)) return;

            Try(() =>
            {
                if (_sdk >= 26)
                {
                    // If amplitude control is missing, waveforms fall back to on/off (amplitudes ignored by the OS),
                    // which the timing array still expresses — so the pattern's rhythm survives.
                    using (var effect = _effectClass.CallStatic<AndroidJavaObject>("createWaveform", p.Timings, p.Amplitudes, -1))
                        _vibrator.Call("vibrate", effect);
                }
                else
                {
                    // Legacy vibrate(long[], repeat) uses an OFF,ON,OFF,ON… contract — incompatible with our
                    // createWaveform (ON, gap, ON…) timings; reusing them inverts the rhythm and can drop a
                    // single-pulse pattern entirely. Below API 26 there's no amplitude anyway, so degrade to one
                    // plain buzz of the pattern's total length.
                    _vibrator.Call("vibrate", p.TotalMs);
                }
            }, $"Android play pattern '{patternId}' failed");
#endif
        }

        public override void RegisterPattern(HapticPattern pattern)
        {
            if (pattern == null) return;
            _patterns ??= new Dictionary<int, AndroidPattern>();
            int key = pattern.ID.GetHashCode();
            if (!_patterns.ContainsKey(key)) _patterns.Add(key, new AndroidPattern(pattern));
        }

        /// <summary>A pattern pre-converted to Android's parallel (timings[], amplitudes[]) arrays.</summary>
        private sealed class AndroidPattern
        {
            public readonly long[] Timings;
            public readonly int[] Amplitudes;
            public readonly long TotalMs;   // sum of all timings — the pre-API-26 single-buzz fallback length

            public AndroidPattern(HapticPattern pattern)
            {
                var timings = new List<long>();
                var amps = new List<int>();
                float previousEnd = 0f;

                foreach (var e in pattern.Pattern)
                {
                    if (e.StartTime > previousEnd)      // silent gap before this event
                    {
                        timings.Add((long)((e.StartTime - previousEnd) * 1000f));
                        amps.Add(0);
                    }
                    timings.Add((long)(e.Duration * 1000f));
                    amps.Add(Mathf.Clamp((int)Mathf.Lerp(1, 255, e.Intensity), 1, 255));
                    previousEnd = e.StartTime + e.Duration;
                }

                Timings = timings.ToArray();
                Amplitudes = amps.ToArray();

                long total = 0;
                for (int k = 0; k < Timings.Length; k++) total += Timings[k];
                TotalMs = total;
            }
        }
    }
}
