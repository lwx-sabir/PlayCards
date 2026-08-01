using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Account
{
    /// <summary>
    /// Drop this on (or beside) a "Sign in with Google" button in ANY scene — Onboarding, Home, a Settings
    /// panel, wherever. It calls the persistent <see cref="KhelaAuthService"/> (which self-bootstraps, so it's
    /// always available regardless of which scene started), disables the button while sign-in is in flight,
    /// and surfaces an optional status message.
    ///
    /// Wire it either way:
    ///   • assign the <c>button</c> field  → its onClick is auto-hooked to <see cref="SignIn"/>, or
    ///   • point the Button's OnClick (inspector) at <see cref="SignIn"/> directly.
    /// </summary>
    public sealed class SocialSignInButton : MonoBehaviour
    {
        [Header("Provider")]
        [SerializeField] private SocialProvider provider = SocialProvider.PlayGames;

        [Header("Optional UI (any may be left empty)")]
        [Tooltip("If set, its onClick is auto-wired to SignIn() and it's disabled while signing in.")]
        [SerializeField] private Button button;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private GameObject busyIndicator;
        [Tooltip("Hide this GameObject once the player is signed in with a social provider.")]
        [SerializeField] private bool hideWhenSignedIn;

        private string _lastFailReason;

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            if (button != null) button.onClick.AddListener(SignIn);
            SetBusy(false);

            // A guest who has already linked a social identity this session shouldn't see a sign-in prompt.
            if (hideWhenSignedIn && KhelaAuthService.Instance != null && KhelaAuthService.Instance.IsSociallySignedIn)
                gameObject.SetActive(false);
        }

        private void OnEnable()
        {
            var svc = KhelaAuthService.Instance;
            if (svc != null) svc.OnSignInFailed += OnServiceFail;
        }

        private void OnDisable()
        {
            var svc = KhelaAuthService.Instance;
            if (svc != null) svc.OnSignInFailed -= OnServiceFail;
        }

        private void OnDestroy()
        {
            if (button != null) button.onClick.RemoveListener(SignIn);
        }

        /// <summary>Wire a Button's OnClick to this if you prefer inspector wiring over the <c>button</c> field.</summary>
        public async void SignIn()
        {
            var svc = KhelaAuthService.Instance;
            if (svc == null)
            {
                // The service self-bootstraps BeforeSceneLoad, so this only happens if the runtime bootstrap
                // was stripped in a build — enter Play via the Boot scene.
                ShowFail("Sign-in unavailable — start from the Boot scene.");
                return;
            }

            SetBusy(true);
            _lastFailReason = null;
            Status("Signing in…");

            bool ok;
            switch (provider)
            {
                case SocialProvider.PlayGames:
                    ok = await svc.SignInWithPlayGamesAsync();
                    break;
                default:
                    // Facebook/Apple sign in with a token from their own SDK, not a plain button tap — call
                    // svc.SignInWithFacebookAsync(token) from that SDK's callback instead of using this component.
                    SetBusy(false);
                    ShowFail($"{provider} sign-in isn't wired to a button yet.");
                    return;
            }

            SetBusy(false);
            if (ok)
            {
                Status("Signed in.");
                if (hideWhenSignedIn) gameObject.SetActive(false);
            }
            else
            {
                Status(_lastFailReason ?? "Sign-in failed. Tap to retry.");
            }
        }

        private void OnServiceFail(string reason) => _lastFailReason = reason;

        private void SetBusy(bool busy)
        {
            if (button != null) button.interactable = !busy;
            if (busyIndicator != null) busyIndicator.SetActive(busy);
        }

        private void Status(string msg)
        {
            if (statusText != null) statusText.text = msg;
        }

        private void ShowFail(string msg)
        {
            Debug.LogWarning($"[SocialSignInButton] {msg}");
            Status(msg);
        }
    }
}
