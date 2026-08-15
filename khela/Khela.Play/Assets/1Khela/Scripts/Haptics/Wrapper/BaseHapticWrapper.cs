using System;
using UnityEngine;

namespace PlayCard.Haptics
{
    /// <summary>
    /// Platform strategy base — one concrete wrapper per platform (Editor / iOS / Android / WebGL). The active one
    /// is chosen once in <see cref="Haptic"/>. Every call reaching a wrapper has ALREADY passed the
    /// <c>Haptic.Enabled</c> gate and the coalesce window, so wrappers only ever talk to the OS.
    /// </summary>
    public abstract class BaseHapticWrapper
    {
        public abstract void Init();

        /// <summary>Play a one-shot. <paramref name="sharpness"/> is used only where the platform supports it (iOS).</summary>
        public abstract void Play(float duration, float intensity, float sharpness);

        public abstract void Play(string patternId);

        public abstract void RegisterPattern(HapticPattern pattern);

        protected static void Log(string message)
        {
            if (Haptic.VerboseLogging) Debug.Log($"[Haptic]: {message}");
        }

        /// <summary>Run a native/JNI call, logging (never throwing) on failure so a haptic can never crash gameplay.</summary>
        protected static void Try(Action action, string errorMessage)
        {
            try { action(); }
            catch (Exception e)
            {
                Debug.LogError($"[Haptic]: {errorMessage}");
                Debug.LogException(e);
            }
        }
    }
}
