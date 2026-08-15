using System.Runtime.InteropServices;
using UnityEngine;

namespace PlayCard.Haptics
{
    /// <summary>
    /// iOS wrapper over the native CoreHaptics plugin (<c>Plugins/iOS/KhelaHaptics.mm</c>). The native side owns a
    /// single <c>CHHapticEngine</c> with stopped/reset handlers that RESTART it after backgrounding or an audio
    /// interruption — so haptics survive an app-switch instead of dying for the session (the original system's worst
    /// bug). Sharpness is passed through (the source hardcoded it to 0).
    /// </summary>
    public sealed class IOSHapticWrapper : BaseHapticWrapper
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void _KhelaHapticInit();
        [DllImport("__Internal")] private static extern void _KhelaHapticPlay(float duration, float intensity, float sharpness);
        [DllImport("__Internal")] private static extern void _KhelaHapticPlayPattern(string patternId);
        [DllImport("__Internal")] private static extern void _KhelaHapticRegisterPattern(string json);
#endif

        public override void Init()
        {
#if UNITY_IOS && !UNITY_EDITOR
            Try(() => _KhelaHapticInit(), "iOS init failed");
#endif
        }

        public override void Play(float duration, float intensity, float sharpness)
        {
#if UNITY_IOS && !UNITY_EDITOR
            Try(() => _KhelaHapticPlay(duration, intensity, sharpness), "iOS play failed");
#endif
        }

        public override void Play(string patternId)
        {
#if UNITY_IOS && !UNITY_EDITOR
            Try(() => _KhelaHapticPlayPattern(patternId), $"iOS play pattern '{patternId}' failed");
#endif
        }

        public override void RegisterPattern(HapticPattern pattern)
        {
            // JsonUtility mirrors the native NSJSONSerialization keys: "ID" + "Pattern" of {Intensity,Sharpness,StartTime,Duration}.
            string json = JsonUtility.ToJson(pattern);
            if (string.IsNullOrEmpty(json)) return;
#if UNITY_IOS && !UNITY_EDITOR
            Try(() => _KhelaHapticRegisterPattern(json), $"iOS register pattern '{pattern.ID}' failed");
#endif
        }
    }
}
