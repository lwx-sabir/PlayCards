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

            // Raise the per-host connection cap from Best's default of 6.
            //
            // Everything in this game talks to ONE host, so that 6 is the app's entire concurrent REST budget. A table
            // burst blows straight through it: joining fires five or six /wallet/balances at once (every HUD refreshes
            // on enable), and each action adds another on top of the action itself. Requests over the cap QUEUE, and a
            // queued request still counts against the 20s client timeout — so it surfaces as "Connection Timed Out!"
            // on a phone whose network is fine and against a server answering every request in under 100ms.
            // "*" is the wildcard host entry, so this covers the API and the hub without naming either.
            Best.HTTP.Shared.HTTPManager.PerHostSettings.Get("*").HostVariantSettings.MaxConnectionPerVariant = 16;
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
#if !DEVELOPMENT_BUILD && !UNITY_EDITOR
            // Release ONLY: block players from summoning the URP Rendering Debugger by accident.
            // Keep it available in Development builds + the editor so we can profile on-device
            // (3-finger tap → Display Stats shows CPU main / render thread / GPU frame times).
            UnityEngine.Rendering.DebugManager.instance.enableRuntimeUI = false;
#endif
        }

        /// <summary>
        /// Spawn Codestage's Advanced FPS Counter once as a keepAlive singleton so the overlay is available in
        /// EVERY build — <b>including release</b> — for on-device profiling. A release/IL2CPP APK performs very
        /// differently from a Development build (managed-code stripping, il2cpp optimizations, no dev logging),
        /// so we must be able to read real fps + memory on the actual shipping binary, not just a dev build.
        ///
        /// It boots VISIBLE in every build — editor, Development, AND release — because the whole point is to read
        /// fps on the real shipping binary. We do NOT hide it behind AFPS's "circle gesture": that gesture is a
        /// ONE-finger drag that has to draw TWO full circles before releasing (see <c>CircleGestureMade</c>) —
        /// undiscoverable and unreliable on-device, so it's useless as the only way to summon the overlay.
        ///
        /// This is fine for the current PRE-LAUNCH profiling phase (no store build yet). BEFORE the first real
        /// store build, hide it from players via the SHIP SWITCH marked below (flip to <c>OperationMode.Disabled</c>,
        /// or delete this whole method). deviceInfoCounter stays off (mostly static clutter).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void SpawnFpsOverlay()
        {
            var afps = CodeStage.AdvancedFPSCounter.AFPSCounter.AddToScene(true); // keepAlive → persists across scene loads
            afps.fpsCounter.Enabled = true;          // FPS + frame ms (CPU frame time) + avg + min/max
            afps.memoryCounter.Enabled = true;       // Mono/heap/total reserved + GFX (GPU) memory
            afps.deviceInfoCounter.Enabled = false;  // device info OFF — mostly static clutter; flip true when tuning the auto-tier GPU table

            // Readability on device: bigger, and inset from the edge so the notch doesn't clip it.
            afps.ScaleFactor = 2f;                       // was ~tiny at 1× on a phone — double the whole overlay
            afps.PaddingOffset = new Vector2(15f, 45f);  // ×2 scale ≈ 30px left inset + 90px top inset (clears notch)
            afps.BackgroundPadding = 6;                  // a touch more background behind the text

            // ===== SHIP SWITCH ===== visible while profiling (all builds incl. release). Before the first store
            // build, change Normal → Disabled here so real players never see an fps counter.
            afps.OperationMode = CodeStage.AdvancedFPSCounter.OperationMode.Normal;
        }

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
