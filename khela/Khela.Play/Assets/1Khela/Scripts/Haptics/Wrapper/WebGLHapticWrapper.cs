using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PlayCard.Haptics
{
    /// <summary>
    /// WebGL wrapper over <c>navigator.vibrate</c> (see <c>Plugins/WebGL/KhelaHaptics.jslib</c>). The Web Vibration
    /// API has no intensity or sharpness — only durations — so those are dropped and only the timing survives. It is
    /// also a no-op on desktop browsers and iOS Safari (which ignore the API), guarded by <c>isMobilePlatform</c>.
    /// </summary>
    public sealed class WebGLHapticWrapper : BaseHapticWrapper
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void _KhelaHapticInit();
        [DllImport("__Internal")] private static extern void _KhelaHapticPlayMs(int durationMs);
        [DllImport("__Internal")] private static extern void _KhelaHapticPlayPattern(string patternId);
        [DllImport("__Internal")] private static extern void _KhelaHapticRegisterPattern(string patternId, int[] pattern, int patternLength);
#endif

        public override void Init()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!Application.isMobilePlatform) return;
            Try(() => _KhelaHapticInit(), "WebGL init failed");
#endif
        }

        public override void Play(float duration, float intensity, float sharpness) // intensity/sharpness unsupported on web
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!Application.isMobilePlatform) return;
            Try(() => _KhelaHapticPlayMs((int)(duration * 1000f)), "WebGL play failed");
#endif
        }

        public override void Play(string patternId)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!Application.isMobilePlatform) return;
            Try(() => _KhelaHapticPlayPattern(patternId), $"WebGL play pattern '{patternId}' failed");
#endif
        }

        public override void RegisterPattern(HapticPattern pattern)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!Application.isMobilePlatform || pattern == null) return;
            int[] timings = ToVibrationPattern(pattern);
            Try(() => _KhelaHapticRegisterPattern(pattern.ID, timings, timings.Length),
                $"WebGL register pattern '{pattern.ID}' failed");
#endif
        }

        /// <summary>Flatten events into a navigator.vibrate timing array [vibrate, pause, vibrate, ...] (ms), starting with a vibration.</summary>
        private static int[] ToVibrationPattern(HapticPattern pattern)
        {
            if (pattern?.Pattern == null || pattern.Pattern.Length == 0) return new int[0];

            var list = new List<int>();
            var events = pattern.Pattern;
            for (int i = 0; i < events.Length; i++)
            {
                if (i > 0)
                {
                    float previousEnd = events[i - 1].StartTime + events[i - 1].Duration;
                    int pause = Mathf.RoundToInt((events[i].StartTime - previousEnd) * 1000f);
                    if (pause > 0) list.Add(pause);
                }
                list.Add(Mathf.RoundToInt(events[i].Duration * 1000f));
            }
            return list.ToArray();
        }
    }
}
