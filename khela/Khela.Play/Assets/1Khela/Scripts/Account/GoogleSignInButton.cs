using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if KHELA_GOOGLE_SIGNIN
using Google;
#endif

namespace PlayCard.Account
{
    /// <summary>
    /// "Sign in with Google" button — the ACCOUNT-PICKER flow, which captures the player's real email
    /// (unlike the Play Games one-tap). Uses the google-signin-unity plugin to obtain a Google ID token, then
    /// hands it to <see cref="KhelaAuthService.SignInWithGoogleAsync"/> (Firebase GoogleAuthProvider →
    /// /api/auth/firebase, which stores the email in LinkedEmail).
    ///
    /// This is SEPARATE from <see cref="SocialSignInButton"/> (Play Games) — put this on your second,
    /// dedicated Google button. Both link onto the same account.
    ///
    /// Gated behind the <c>KHELA_GOOGLE_SIGNIN</c> scripting define so the project keeps compiling BEFORE the
    /// plugin is imported. After importing google-signin-unity, add <c>KHELA_GOOGLE_SIGNIN</c> under
    /// Player Settings → Scripting Define Symbols (Android) to activate the real flow.
    /// </summary>
    public sealed class GoogleSignInButton : MonoBehaviour
    {
        [Tooltip("Web (server) OAuth client ID — the SAME value as GameInfo.WebClientId.")]
        [SerializeField] private string webClientId = "95791701464-1vtoo992htv6tnmvpts24ij24bg95lkm.apps.googleusercontent.com";

        [Header("Optional UI (any may be left empty)")]
        [SerializeField] private Button button;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private GameObject busyIndicator;

        private bool _busy;

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            if (button != null) button.onClick.AddListener(SignIn);
            SetBusy(false);
        }

        private void OnDestroy()
        {
            if (button != null) button.onClick.RemoveListener(SignIn);
        }

        /// <summary>Wire a Button's OnClick here, or assign the <c>button</c> field to auto-hook it.</summary>
        public async void SignIn()
        {
            if (_busy) return;

            var svc = KhelaAuthService.Instance;
            if (svc == null)
            {
                Status("Sign-in unavailable — start from the Boot scene.");
                return;
            }

#if KHELA_GOOGLE_SIGNIN && UNITY_ANDROID && !UNITY_EDITOR
            _busy = true;
            SetBusy(true);
            Status("Signing in…");
            try
            {
                GoogleSignIn.Configuration = new GoogleSignInConfiguration
                {
                    WebClientId = webClientId,
                    RequestEmail = true,        // <-- the whole point: get the real email
                    RequestIdToken = true,      // Firebase needs the ID token
                    UseGameSignIn = false       // account picker, not Play Games
                };

                GoogleSignInUser gUser = await GoogleSignIn.DefaultInstance.SignIn();
                if (gUser == null || string.IsNullOrEmpty(gUser.IdToken))
                {
                    Status("Google sign-in returned no token.");
                    return;
                }

                bool ok = await svc.SignInWithGoogleAsync(gUser.IdToken);
                Status(ok ? "Signed in." : "Sign-in failed. Tap to retry.");
            }
            catch (System.Exception ex)
            {
                // google-signin-unity throws GoogleSignIn.SignInException on cancel/error.
                Debug.LogWarning($"[GoogleSignInButton] {ex.Message}");
                Status("Google sign-in cancelled or failed.");
            }
            finally
            {
                _busy = false;
                SetBusy(false);
            }
#else
            Status("Google Sign-In not enabled — import google-signin-unity + add the KHELA_GOOGLE_SIGNIN define.");
            await Task.CompletedTask;
#endif
        }

        private void SetBusy(bool busy)
        {
            if (button != null) button.interactable = !busy;
            if (busyIndicator != null) busyIndicator.SetActive(busy);
        }

        private void Status(string msg)
        {
            if (statusText != null) statusText.text = msg;
            Debug.Log($"[GoogleSignInButton] {msg}");
        }
    }
}
