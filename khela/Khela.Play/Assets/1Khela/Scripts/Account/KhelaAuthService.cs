using System;
using System.Threading.Tasks;
using Firebase;
using Firebase.Auth;
using Firebase.Extensions;
using Khela.Common.Auth;
using UnityEngine;
// GooglePlayGames.BasicApi also declares an AuthResponse, so alias ours to keep the two unambiguous.
using KhelaAuthResponse = Khela.Common.Auth.AuthResponse;
#if UNITY_ANDROID
using GooglePlayGames;
using GooglePlayGames.BasicApi;
#endif

namespace PlayCard.Account
{
    /// <summary>Which social provider a sign-in came from (for analytics / UI state).</summary>
    public enum SocialProvider
    {
        PlayGames,
        Facebook,
        Apple
    }

    /// <summary>
    /// THE single sign-in service for every social provider.
    ///
    /// Firebase Auth is used as the broker: whichever provider the player picks (Play Games today,
    /// Facebook/Apple later) is turned into a Firebase credential, and Firebase hands back ONE kind of
    /// token — a Firebase ID token. That single token is posted to <c>/api/auth/firebase</c>, which
    /// verifies it server-side and returns our normal app JWT. Adding a provider therefore means adding
    /// one method here, not a new backend integration.
    ///
    /// Guest -> social upgrade: the exchange call carries the player's CURRENT app JWT and DeviceId, so the
    /// server links the social identity onto the guest account they've already been playing as. Their chips
    /// and progress carry over instead of a second, empty account being created.
    ///
    /// Gameplay code should keep reading <see cref="AccountManager.JwtToken"/> — this service just swaps the
    /// token underneath via <see cref="AccountManager.ApplyExternalAuth"/>.
    /// </summary>
    public sealed class KhelaAuthService : MonoBehaviour
    {
        [Header("API")]
        [SerializeField] private string firebaseEndpoint = "/api/auth/firebase";

        [Header("Behaviour")]
        [Tooltip("Force-refresh the Play Games server auth code. Leave off; on wastes a round trip.")]
        [SerializeField] private bool forceRefreshServerCode = false;

        public static KhelaAuthService Instance { get; private set; }

        /// <summary>True when a social identity is currently attached (as opposed to a plain device guest).</summary>
        public bool IsSociallySignedIn => _auth != null && _auth.CurrentUser != null;

        /// <summary>Raised after a successful sign-in + backend exchange (token already applied).</summary>
        public event Action<SocialProvider> OnSignedIn;

        /// <summary>Raised with a human-readable reason when sign-in fails or is cancelled.</summary>
        public event Action<string> OnSignInFailed;

        private FirebaseAuth _auth;
        private bool _firebaseReady;
        private bool _busy;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ===================== Public API — one method per provider =====================

        /// <summary>
        /// Google / Play Games sign-in (Android). <paramref name="interactive"/> false attempts the silent
        /// "already signed into Play Games" path — use it on boot; use true behind an explicit button.
        /// </summary>
        public async Task<bool> SignInWithPlayGamesAsync(bool interactive = true)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_busy) return false;
            _busy = true;
            try
            {
                if (!await EnsureFirebaseAsync()) return false;

                var status = await AuthenticatePlayGamesAsync(interactive);
                if (status != SignInStatus.Success)
                {
                    Fail(status == SignInStatus.Canceled
                        ? "Play Games sign-in was cancelled."
                        : "Play Games sign-in failed.");
                    return false;
                }

                var serverAuthCode = await RequestPlayGamesServerCodeAsync();
                if (string.IsNullOrEmpty(serverAuthCode))
                {
                    // Almost always a config problem: the Web Client ID in GameInfo.cs must match the OAuth
                    // web client registered as the Play Games "game server" credential.
                    Fail("Could not get a Play Games server auth code (check the Web Client ID setup).");
                    return false;
                }

                var credential = PlayGamesAuthProvider.GetCredential(serverAuthCode);
                return await CompleteSignInAsync(credential, SocialProvider.PlayGames, "playgames");
            }
            finally
            {
                _busy = false;
            }
#else
            await Task.CompletedTask;
            Fail("Play Games sign-in is only available on an Android device.");
            return false;
#endif
        }

        /// <summary>
        /// Facebook sign-in. Takes the access token from whichever Facebook SDK is installed, so this
        /// service stays free of a hard SDK dependency — wire the SDK's callback straight into this.
        /// </summary>
        public async Task<bool> SignInWithFacebookAsync(string facebookAccessToken)
        {
            if (_busy) return false;
            if (string.IsNullOrEmpty(facebookAccessToken))
            {
                Fail("Missing Facebook access token.");
                return false;
            }

            _busy = true;
            try
            {
                if (!await EnsureFirebaseAsync()) return false;

                var credential = FacebookAuthProvider.GetCredential(facebookAccessToken);
                return await CompleteSignInAsync(credential, SocialProvider.Facebook, "facebook");
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// Drops the social identity locally. Deliberately does NOT touch the app JWT — the player stays
        /// signed into their Khela account; signing out of Firebase only detaches the provider session.
        /// </summary>
        public void SignOutSocial()
        {
            try { _auth?.SignOut(); }
            catch (Exception ex) { Debug.LogWarning($"[KhelaAuthService] Firebase sign-out failed: {ex.Message}"); }
        }

        // ===================== Core: credential -> Firebase -> our backend =====================

        private async Task<bool> CompleteSignInAsync(Credential credential, SocialProvider provider, string analyticsMethod)
        {
            FirebaseUser user;
            string idToken;
            try
            {
                var result = await OnMainThread(_auth.SignInAndRetrieveDataWithCredentialAsync(credential));
                user = result?.User;
                if (user == null)
                {
                    Fail("Sign-in did not return a user.");
                    return false;
                }

                idToken = await OnMainThread(user.TokenAsync(false));
            }
            catch (Exception ex)
            {
                Fail($"Firebase sign-in failed: {Unwrap(ex)}");
                return false;
            }

            if (string.IsNullOrEmpty(idToken))
            {
                Fail("Firebase returned an empty ID token.");
                return false;
            }

            return await ExchangeWithBackendAsync(idToken, provider, analyticsMethod);
        }

        /// <summary>
        /// Trades the Firebase ID token for our app JWT. Sends the current app JWT + DeviceId so the server
        /// can upgrade this device's guest account in place rather than creating a second one.
        /// </summary>
        private async Task<bool> ExchangeWithBackendAsync(string idToken, SocialProvider provider, string analyticsMethod)
        {
            var account = AccountManager.Instance;
            if (account == null)
            {
                Fail("AccountManager is not in the scene.");
                return false;
            }

            var request = new FirebaseAuthRequest
            {
                IdToken = idToken,
                DeviceId = account.DeviceId ?? string.Empty,
                CountryCode = account.GetCountryCode()
            };

            var response = await account.PostJsonAsync<KhelaAuthResponse>(
                firebaseEndpoint, request, expectResponseBody: true, bearerToken: account.JwtToken);

            if (response == null || string.IsNullOrEmpty(response.Token))
            {
                Fail("The server rejected the sign-in.");
                return false;
            }

            account.ApplyExternalAuth(response, analyticsMethod);
            OnSignedIn?.Invoke(provider);
            Debug.Log($"[KhelaAuthService] Signed in via {provider} as {response.Username} ({response.UserId}).");
            return true;
        }

        // ===================== Firebase / Play Games plumbing =====================

        private async Task<bool> EnsureFirebaseAsync()
        {
            if (_firebaseReady && _auth != null) return true;

            try
            {
                var status = await OnMainThread(FirebaseApp.CheckAndFixDependenciesAsync());
                if (status != DependencyStatus.Available)
                {
                    Fail($"Firebase is unavailable on this device ({status}).");
                    return false;
                }

                _auth = FirebaseAuth.DefaultInstance;
                _firebaseReady = true;
                return true;
            }
            catch (Exception ex)
            {
                Fail($"Firebase init failed: {Unwrap(ex)}");
                return false;
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static Task<SignInStatus> AuthenticatePlayGamesAsync(bool interactive)
        {
            var tcs = new TaskCompletionSource<SignInStatus>();

            // Authenticate() = silent (player already signed into Play Games); ManuallyAuthenticate() shows UI.
            // Silent first is what makes the "0-click" sign-in feel instant on boot.
            if (interactive)
                PlayGamesPlatform.Instance.ManuallyAuthenticate(status => tcs.TrySetResult(status));
            else
                PlayGamesPlatform.Instance.Authenticate(status => tcs.TrySetResult(status));

            return tcs.Task;
        }

        private Task<string> RequestPlayGamesServerCodeAsync()
        {
            var tcs = new TaskCompletionSource<string>();
            PlayGamesPlatform.Instance.RequestServerSideAccess(forceRefreshServerCode, code => tcs.TrySetResult(code));
            return tcs.Task;
        }
#endif

        /// <summary>
        /// Firebase completes its Tasks on a background thread. Hop back to the Unity main thread before
        /// continuing, so everything after an await (Best.HTTP calls, Unity API, events) is safe to touch.
        /// </summary>
        private static Task<T> OnMainThread<T>(Task<T> task)
        {
            var tcs = new TaskCompletionSource<T>();
            task.ContinueWithOnMainThread(t =>
            {
                if (t.IsCanceled) tcs.TrySetCanceled();
                else if (t.IsFaulted) tcs.TrySetException(t.Exception ?? (Exception)new InvalidOperationException("Unknown Firebase error."));
                else tcs.TrySetResult(t.Result);
            });
            return tcs.Task;
        }

        private void Fail(string reason)
        {
            Debug.LogWarning($"[KhelaAuthService] {reason}");
            OnSignInFailed?.Invoke(reason);
        }

        /// <summary>Firebase surfaces the useful message inside an AggregateException — dig it out.</summary>
        private static string Unwrap(Exception ex)
        {
            if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
                return agg.InnerExceptions[0].Message;
            return ex.Message;
        }
    }
}
