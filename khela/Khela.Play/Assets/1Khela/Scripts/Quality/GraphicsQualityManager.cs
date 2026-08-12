using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using PlayCard.App;   // SceneFrameRate — for the "no scene override → tier default fps" fallback

namespace PlayCard.Quality
{
    /// <summary>
    /// Single source of truth for the graphics quality tier (Low / Mid / High / Ultra). See
    /// docs/GRAPHICS_QUALITY.md.
    ///
    /// The important guarantee: it applies the saved (or auto-detected) tier **before the first scene loads,
    /// every run**, via <see cref="RuntimeInitializeLoadType.BeforeSceneLoad"/>. Because
    /// <see cref="QualitySettings"/> is global and persists once set, that makes EVERY scene render at the
    /// chosen tier — Home, Lobby, Table, World — including a scene you play directly in the editor. (Before
    /// this, only the Home settings panel ever applied a tier, so quality "only worked in Home".)
    ///
    /// <see cref="SetTier"/> swaps the URP asset (via <c>QualitySettings.SetQualityLevel</c>, looked up by
    /// the level NAME so index order doesn't matter), sets the FPS ceiling, persists the choice, and raises
    /// <see cref="OnTierChanged"/> so listeners (post-processing, etc.) can react.
    /// </summary>
    public static class GraphicsQualityManager
    {
        public enum Tier { Low = 0, Mid = 1, High = 2, Ultra = 3 }

        private const string PrefKey = "khela.gfxTier";
        // Must match the Quality level NAMES in Project Settings ▸ Quality.
        private static readonly string[] LevelNames = { "Low", "Mid", "High", "Ultra" };

        /// <summary>The tier currently applied.</summary>
        public static Tier Current { get; private set; } = Tier.Mid;

        /// <summary>Raised whenever the tier changes (also on the startup apply). Args: the new tier.</summary>
        public static event Action<Tier> OnTierChanged;

        /// <summary>
        /// DEFAULT target FPS for a tier — applied only to scenes that carry NO <see cref="SceneFrameRate"/>.
        /// A scene with a SceneFrameRate overrides this entirely (its value wins). Tunable — e.g. bump Ultra
        /// to 90/120 for high-refresh flagships.
        /// </summary>
        public static int DefaultFps(Tier tier) => tier switch
        {
            Tier.Low   => 30,
            Tier.Mid   => 30,
            Tier.High  => 60,
            Tier.Ultra => 60,
            _          => 60,
        };

        /// <summary>
        /// Runs once before the first scene of EVERY play session (editor or build) — this is what makes the
        /// tier apply everywhere instead of only where a settings panel lives.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyOnStartup()
        {
            Apply(ResolveTier(), persist: false);
            SceneManager.sceneLoaded -= OnSceneLoaded;   // guard against a domain-reload double-subscribe
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// Every scene load: re-assert the tier if the scene somehow drifted it, and LOG the active pipeline +
        /// render scale + MSAA. This is what makes "is Low actually applying in Table/World?" answerable — watch
        /// the Console when you enter each scene. If it prints <c>Mobile_Low_URP, renderScale 0.65</c> then Low
        /// IS applied (a scene that still looks clean at 0.65 just has baked lighting doing the heavy lifting).
        /// </summary>
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            int want = LevelIndex(Current);
            int before = QualitySettings.GetQualityLevel();
            if (want >= 0 && before != want)
            {
                QualitySettings.SetQualityLevel(want, true);
                Debug.LogWarning($"[GfxQuality] scene '{scene.name}' loaded at quality level {before} — " +
                                 $"re-asserted {Current} (level {want}). Something changed the level on load.");
            }

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            Debug.Log($"[GfxQuality] scene '{scene.name}': tier {Current}, pipeline " +
                      $"{(urp != null ? urp.name : "<none>")}, renderScale {(urp != null ? urp.renderScale : -1f)}, " +
                      $"MSAA {(urp != null ? urp.msaaSampleCount : -1)}");

            // FPS fallback: a SceneFrameRate in the scene runs in OnEnable (BEFORE this event) and its value
            // wins. Only when the scene carries NO SceneFrameRate do we apply the tier's default fps.
            if (UnityEngine.Object.FindObjectsByType<SceneFrameRate>(FindObjectsSortMode.None).Length == 0)
            {
                Application.targetFrameRate = DefaultFps(Current);
                Debug.Log($"[GfxQuality] '{scene.name}' has no SceneFrameRate → tier default {DefaultFps(Current)} fps.");
            }
        }

        private static int LevelIndex(Tier tier) => Array.FindIndex(QualitySettings.names,
            n => string.Equals(n, LevelNames[(int)tier], StringComparison.OrdinalIgnoreCase));

        /// <summary>Set the tier explicitly (e.g. from the settings menu). Persists by default.</summary>
        public static void SetTier(Tier tier, bool persist = true) => Apply(tier, persist);

        /// <summary>Clear the saved override and fall back to auto-detect.</summary>
        public static void ClearSaved()
        {
            PlayerPrefs.DeleteKey(PrefKey);
            Apply(ResolveTier(), persist: false);
        }

        private static Tier ResolveTier()
        {
            if (PlayerPrefs.HasKey(PrefKey))
                return (Tier)Mathf.Clamp(PlayerPrefs.GetInt(PrefKey), 0, 3);
            return AutoDetect();
        }

        private static void Apply(Tier tier, bool persist)
        {
            int idx = LevelIndex(tier);
            if (idx < 0)
            {
                Debug.LogError($"[GfxQuality] Quality level '{LevelNames[(int)tier]}' not found — check the " +
                               "names in Project Settings ▸ Quality (expected Low / Mid / High / Ultra).");
                return;
            }

            QualitySettings.SetQualityLevel(idx, true);   // true = apply now → swaps the level's URP asset
            // The tier controls VISUAL quality only. It deliberately does NOT touch targetFrameRate/vSync —
            // fps is owned by SceneFrameRate (per scene) + MobileBootstrap (boot default), independent of tier.

            if (persist)
            {
                PlayerPrefs.SetInt(PrefKey, (int)tier);
                PlayerPrefs.Save();
            }

            Current = tier;
            OnTierChanged?.Invoke(tier);

            var urp = GraphicsSettings.currentRenderPipeline;
            Debug.Log($"[GfxQuality] {tier} (level {idx}); pipeline = {(urp != null ? urp.name : "<none>")}");
        }

        /// <summary>
        /// Conservative starter auto-detect. GPU family/model is the primary signal (RAM is a poor GPU proxy —
        /// a 6 GB tablet can have a weak Adreno 610). Tune the thresholds against real devices; a runtime
        /// FPS safety-net (planned) will catch misdetections. Editor/desktop default to High for convenience.
        /// </summary>
        private static Tier AutoDetect()
        {
#if UNITY_EDITOR || UNITY_STANDALONE
            return Tier.High;
#else
            string gpu = (SystemInfo.graphicsDeviceName ?? string.Empty).ToLowerInvariant();

            if (gpu.Contains("apple"))          // A-series / M-series are strong
                return Tier.High;

            int adreno = FirstNumberAfter(gpu, "adreno");
            if (adreno > 0)
            {
                if (adreno >= 730) return Tier.Ultra;   // 7xx elite
                if (adreno >= 640) return Tier.High;    // 6xx mid/high, low 7xx
                if (adreno >= 620) return Tier.Mid;     // 6xx low-mid
                return Tier.Low;                        // 610 / 612 / 619 and below
            }

            int mali = FirstNumberAfter(gpu, "mali-g");
            if (mali > 0)
            {
                if (mali >= 78) return Tier.High;       // G78 / G710+ (3-digit parse ≥78)
                if (mali >= 68) return Tier.Mid;
                return Tier.Low;                        // G57 and below
            }

            // Unknown GPU → weak RAM/core fallback, default Mid.
            int ram = SystemInfo.systemMemorySize, cores = SystemInfo.processorCount;
            if (ram >= 6000 && cores >= 8) return Tier.High;
            if (ram >= 4000 && cores >= 6) return Tier.Mid;
            return Tier.Low;
#endif
        }

        /// <summary>First run of digits after <paramref name="token"/> in <paramref name="s"/>, or -1.</summary>
        private static int FirstNumberAfter(string s, string token)
        {
            int i = s.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) return -1;
            i += token.Length;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            int start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            return i > start && int.TryParse(s.Substring(start, i - start), out var n) ? n : -1;
        }
    }
}
