using System;
using UnityEngine;

namespace PlayCard.Haptics
{
    /// <summary>
    /// Khela's haptic feedback facade — a single entry point for every buzz in the game. Ported from the Watermelon
    /// Core design (genuine CoreHaptics on iOS, <c>VibrationEffect</c> amplitude on Android) and owned here, with the
    /// weaknesses of the original fixed:
    ///   • ONE persisted toggle (<see cref="Enabled"/>, PlayerPref <c>khela.haptics</c>) — no second desynced switch.
    ///   • Semantic presets (<see cref="HapticType"/>) so call-sites read by meaning; none are silent.
    ///   • A global coalesce gate (<see cref="MinInterval"/>) so high-frequency callers don't compound/thrash.
    ///   • iOS engine that restarts after interruption; Android capability guards — handled in the wrappers.
    ///
    /// Every path is null/enabled-guarded, so calling it on an unsupported platform or with haptics off is a safe
    /// no-op. Self-initialises before the first scene; also lazily initialises on first use.
    /// </summary>
    public static class Haptic
    {
        private const string PrefKey = "khela.haptics";

        // ---- persisted enable toggle: the single source of truth both settings screens drive ----
        private static bool _enabled = true;
        public static event Action<bool> OnEnabledChanged;

        public static bool Enabled
        {
            get { EnsureInit(); return _enabled; }
            set
            {
                EnsureInit();
                if (_enabled == value) return;
                _enabled = value;
                PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
                PlayerPrefs.Save();
                OnEnabledChanged?.Invoke(value);
            }
        }

        /// <summary>Route wrapper diagnostics to the console. Off by default.</summary>
        public static bool VerboseLogging { get; set; }

        public static bool IsInitialized { get; private set; }

        /// <summary>
        /// Minimum seconds between one-shots (the global coalesce window). Stops player-hit spam from compounding on
        /// iOS or last-wins-thrashing on Android. Set 0 to disable. Patterns are deliberate and bypass this.
        /// </summary>
        public static float MinInterval = 0.03f;

        private static float _lastPlay = -999f;
        private static BaseHapticWrapper _wrapper;

        // ---- semantic presets: (duration, intensity, sharpness). Tuned to be perceptible — no 0-intensity taps. ----
        // Order MUST match the HapticType enum.
        private static readonly HapticData[] Presets =
        {
            /* Selection    */ new HapticData(0.02f, 0.35f, 0.70f),
            /* Success      */ new HapticData(0.10f, 0.60f, 0.50f),
            /* Warning      */ new HapticData(0.12f, 0.70f, 0.40f),
            /* Failure      */ new HapticData(0.16f, 0.90f, 0.30f),
            /* LightImpact  */ new HapticData(0.03f, 0.40f, 0.50f),
            /* MediumImpact */ new HapticData(0.05f, 0.65f, 0.50f),
            /* HeavyImpact  */ new HapticData(0.08f, 0.90f, 0.50f),
            /* RigidImpact  */ new HapticData(0.03f, 0.80f, 0.90f),
            /* SoftImpact   */ new HapticData(0.09f, 0.50f, 0.10f),
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInit() => EnsureInit();

        private static void EnsureInit()
        {
            if (IsInitialized) return;
            IsInitialized = true;

            _enabled = PlayerPrefs.GetInt(PrefKey, 1) == 1;

            _wrapper = SelectWrapper();
            if (_wrapper == null)
            {
                if (VerboseLogging) Debug.Log("[Haptic]: unsupported platform — haptics are a no-op.");
                return;
            }
            _wrapper.Init();   // prewarms the iOS engine early → lower first-tap latency
        }

        private static BaseHapticWrapper SelectWrapper()
        {
#if UNITY_EDITOR
            return new EditorHapticWrapper();
#elif UNITY_IOS
            return new IOSHapticWrapper();
#elif UNITY_ANDROID
            return new AndroidHapticWrapper();
#elif UNITY_WEBGL
            return new WebGLHapticWrapper();
#else
            return null;
#endif
        }

        // ---------------------------------------------------------------- public play API

        /// <summary>Play a semantic feedback (the everyday call — <c>Haptic.Play(HapticType.Success)</c>).</summary>
        public static void Play(HapticType type)
        {
            var d = Presets[(int)type];
            PlayInternal(d.Duration, d.Intensity, d.Sharpness);
        }

        /// <summary>Play an explicit one-shot.</summary>
        public static void Play(HapticData data)
        {
            if (data != null) PlayInternal(data.Duration, data.Intensity, data.Sharpness);
        }

        /// <summary>Play an explicit one-shot. Sharpness matters only on iOS.</summary>
        public static void Play(float duration, float intensity = 1f, float sharpness = 0f)
            => PlayInternal(duration, intensity, sharpness);

        private static void PlayInternal(float duration, float intensity, float sharpness)
        {
            EnsureInit();
            if (!_enabled || _wrapper == null) return;
            if (duration <= 0f) return;
            if (!Gate()) return;
            _wrapper.Play(duration, Mathf.Clamp01(intensity), Mathf.Clamp01(sharpness));
        }

        // ---- patterns (deliberate; NOT rate-limited by the coalesce gate) ----

        public static void RegisterPattern(HapticPattern pattern)
        {
            EnsureInit();
            if (_wrapper != null && pattern != null) _wrapper.RegisterPattern(pattern);
        }

        public static void Play(HapticPattern pattern)
        {
            if (pattern != null) PlayPattern(pattern.ID);
        }

        public static void PlayPattern(string patternId)
        {
            EnsureInit();
            if (_enabled && _wrapper != null && !string.IsNullOrEmpty(patternId)) _wrapper.Play(patternId);
        }

        // ---- convenience aliases (readable call-sites) ----
        public static void Selection() => Play(HapticType.Selection);
        public static void Success() => Play(HapticType.Success);
        public static void Warning() => Play(HapticType.Warning);
        public static void Failure() => Play(HapticType.Failure);
        public static void LightImpact() => Play(HapticType.LightImpact);
        public static void MediumImpact() => Play(HapticType.MediumImpact);
        public static void HeavyImpact() => Play(HapticType.HeavyImpact);

        private static bool Gate()
        {
            if (MinInterval <= 0f) return true;
            float now = Time.unscaledTime;
            if (now - _lastPlay < MinInterval) return false;
            _lastPlay = now;
            return true;
        }
    }
}
