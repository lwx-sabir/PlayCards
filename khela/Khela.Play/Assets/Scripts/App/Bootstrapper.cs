using PlayCard.Account;
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

        // Start (not Awake): guarantees AccountManager.Awake has already run and set up Instance + auth.
        private void Start()
        {
            var acc = AccountManager.Instance;
            if (acc == null)
            {
                Debug.LogError("[Bootstrapper] No AccountManager in the Boot scene — add one (and WalletManager) before this.");
                return;
            }

            if (acc.IsReady) { GoHome(); return; }

            acc.OnReady += GoHome;
            Invoke(nameof(GoHomeOnTimeout), authTimeoutSeconds);
        }

        private void OnDisable()
        {
            if (AccountManager.Instance != null) AccountManager.Instance.OnReady -= GoHome;
            CancelInvoke();
        }

        private void GoHomeOnTimeout()
        {
            if (_loaded) return;
            Debug.LogWarning("[Bootstrapper] Auth not ready before timeout — loading Home anyway.");
            GoHome();
        }

        private void GoHome()
        {
            if (_loaded) return;     // OnReady + the timeout can both fire; only navigate once
            _loaded = true;
            SceneNavigator.GoToHome();
        }
    }
}
