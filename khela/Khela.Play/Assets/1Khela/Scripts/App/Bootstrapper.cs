using System;
using PlayCard.Account;
using PlayCard.Avatar;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.App
{
    /// <summary>
    /// Boot-scene composition root (Boot is build index 0, the app's true entry point). Lives in the Boot
    /// scene alongside the persistent singletons (<see cref="AccountManager"/> + WalletManager, both
    /// DontDestroyOnLoad). It waits for the device-guest auth to finish, then loads Home. A timeout net
    /// loads Home anyway so a slow/dead backend never leaves the player stranded on a blank Boot scene.
    /// </summary>
    public sealed class Bootstrapper : MonoBehaviour
    {
        [Tooltip("Load Home after this long even if auth hasn't completed (so the UI still appears).")]
        [SerializeField] private float authTimeoutSeconds = 8f;

        private bool _loaded;

        /// <summary>
        /// Initialize the Best networking stack ONCE, before any scene/Awake — so it's ready before the first HTTPS/WSS
        /// call (AccountManager's device-guest auth). HTTPManager self-inits on first use, but Best TLS Security must be
        /// registered explicitly for the BouncyCastle TLS on IL2CPP (Android/iOS). Skipped on WebGL (the browser does TLS).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitBestNetworking()
        {
            Best.HTTP.Shared.HTTPManager.Setup();
            Best.HTTP.Shared.HTTPManager.Logger.Level = Best.HTTP.Shared.Logger.Loglevels.Warning;
            // Filter out the one benign Best.SignalR teardown NRE so it doesn't spam the console or get recorded as a
            // non-fatal by crash reporting. Everything else (incl. real SignalR errors) still logs. See BestNetLogFilter.
            PlayCard.Core.BestNetLogFilter.Install();
#if !UNITY_WEBGL || UNITY_EDITOR
            Best.TLSSecurity.TLSSecurity.Setup();
#endif
        }

        /// <summary>
        /// Disable URP's runtime Rendering Debugger ("Display Stats" overlay). It ships enabled in every build and
        /// is summoned by a 3-finger tap on mobile (Ctrl+Backspace on desktop) — players trigger it by accident.
        /// Turning off <c>enableRuntimeUI</c> makes the gesture inert without affecting anything we render.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void DisableRenderingDebugger()
        {
            UnityEngine.Rendering.DebugManager.instance.enableRuntimeUI = false;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Dev/QA ONLY: spawn Codestage's Advanced FPS Counter once as a keepAlive singleton, so FPS + device
        /// info show across every scene (Boot → Home → Table → Worlds). The whole method is compiled OUT of
        /// release builds by the guard, so players never see it — a release APK carries no overlay.
        ///
        /// Device-info readout mirrors the RAM/VRAM/core values <c>PostFxTierController.DetectTier()</c> reads,
        /// so on real hardware you can confirm the auto-tier thresholds land where intended; the FPS readout is
        /// how you profile the Forward+ / cascade mobile cost and verify the per-scene SceneFrameRate caps.
        ///
        /// Toggle: backquote on desktop (the AFPS asmdef auto-defines AFPS_INPUT_SYSTEM for the Input System
        /// package, so its new-Input path is live), or a two-finger circle gesture on device.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void SpawnDevFpsOverlay()
        {
            var afps = CodeStage.AdvancedFPSCounter.AFPSCounter.AddToScene(true); // keepAlive → persists across scene loads
            afps.fpsCounter.Enabled = true;
            afps.deviceInfoCounter.Enabled = true;   // CPU/GPU/RAM/VRAM/DPI — cross-check the auto-tier thresholds
            afps.memoryCounter.Enabled = false;      // off by default (noisy); flip on when hunting GC allocations
            afps.circleGesture = true;               // two-finger circle toggles the overlay on device
        }
#endif

        // Start (not Awake): guarantees AccountManager.Awake has already run and set up Instance + auth.
        private void Start()
        {
            var acc = AccountManager.Instance;
            if (acc == null)
            {
                Debug.LogError("[Bootstrapper] No AccountManager in the Boot scene — add one (and WalletManager) before this.");
                return;
            }

            if (acc.IsReady) { RouteAfterAuth(); return; }

            acc.OnReady += RouteAfterAuth;
            Invoke(nameof(GoHomeOnTimeout), authTimeoutSeconds);
        }

        private void OnDisable()
        {
            if (AccountManager.Instance != null) AccountManager.Instance.OnReady -= RouteAfterAuth;
            CancelInvoke();
        }

        private void GoHomeOnTimeout()
        {
            if (_loaded) return;
            Debug.LogWarning("[Bootstrapper] Auth not ready before timeout — loading Home anyway.");
            Navigate(SceneNavigator.Home);
        }

        /// <summary>
        /// After auth, ask the server whether this player already has an avatar. First-run players (GET ok + no avatar)
        /// go to the Onboarding picker; everyone else goes Home. Any error (offline, slow) falls through to Home so a
        /// returning player is never trapped on Boot. The fetched avatar is cached so Home/seats can render it.
        /// </summary>
        private async void RouteAfterAuth()
        {
            if (_loaded) return;

            string target = SceneNavigator.Home;
            try
            {
                var r = await BlackjackRestClient.Instance.GetMyAvatarAsync();
                if (r.Ok)
                {
                    AvatarService.Instance.SetMine(r.Value);   // seed the cache (avoids a second GET on Home)
                    if (r.Value == null || string.IsNullOrEmpty(r.Value.BaseId))
                        target = SceneNavigator.Onboarding;    // no avatar yet → first-run picker
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bootstrapper] avatar check failed, going Home: {e.Message}");
            }

            Navigate(target);
        }

        private void Navigate(string scene)
        {
            if (_loaded) return;     // OnReady/router + the timeout can race; only navigate once
            _loaded = true;
            if (scene == SceneNavigator.Onboarding) SceneNavigator.GoToOnboarding();
            else SceneNavigator.GoToHome();
        }
    }
}
