using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Best.HTTP;
using PlayCard.App;    // SceneNavigator — last-resort restart when re-authentication fails
using PlayCard.Core;
using UnityEngine;
using Khela.Common;
using Khela.Common.Auth;
namespace PlayCard.Account
{
    /// <summary>
    /// Handles device fingerprint registration, guest account creation, login, and token refresh.
    /// Stores credentials locally via SaveController/ISaveObject.
    /// </summary>
    public class AccountManager : MonoBehaviour
    {
        [Header("API")]
        [SerializeField] private string baseApiUrl = "https://your-api.example.com";
        [SerializeField] private string registerEndpoint = "/api/auth/register";
        [SerializeField] private string loginEndpoint = "/api/auth/login";
        [SerializeField] private string deviceRegisterEndpoint = "/api/device/register";

        [Header("Timing")]
        [SerializeField] private float autoSaveIntervalSeconds = 60f;
        [SerializeField] private int signupRetryCount = 1;
        [SerializeField] private int tokenRefreshSkewSeconds = 60; // refresh slightly before expiry

        public static AccountManager Instance { get; private set; }

        public bool IsReady { get; private set; }
        public string JwtToken => _authSave.Token;
        public string UserId => _authSave.UserId;

        /// <summary>Server-side device registration id — sent on social sign-in so the server can link the
        /// social identity onto THIS device's existing guest account (guest -> social upgrade).</summary>
        public string DeviceId => _authSave.DeviceId;

        public event Action OnReady;
        public event Action OnTokenRefreshed;

        // System.Text.Json so the property-based Khela.Common.Auth DTOs (de)serialize correctly and the
        // server's camelCase JSON binds case-insensitively. Unity's JsonUtility does NEITHER — it only
        // handles public fields and is case-sensitive — which silently broke the entire auth bootstrap.
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private AuthSave _authSave = new AuthSave();

        private bool _refreshing;
        private float _nextRefreshCheck;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            SaveController.Init(autoSaveIntervalSeconds: autoSaveIntervalSeconds);
            SaveController.Register(_authSave);

            KhelaAnalytics.LogAppOpen();

            // Fire-and-forget async init
            _ = InitializeAsync();
        }

        // Proactively keep the JWT fresh. The 60-min token would otherwise expire mid-session and 401 EVERY
        // endpoint (only Blackjack/Tcp REST re-auth reactively). Re-issue ~2 min before expiry so no request
        // ever carries a dead token. Cheap: real work runs at most every 15s, and only when near expiry.
        private void Update()
        {
            if (!IsReady || _refreshing || Time.unscaledTime < _nextRefreshCheck) return;
            _nextRefreshCheck = Time.unscaledTime + 15f;

            var secondsLeft = _authSave.ExpiresAtUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (secondsLeft <= tokenRefreshSkewSeconds * 2)
                _ = ProactiveRefreshAsync();
        }

        // Update() is frozen while the app is backgrounded, so a token can lapse during a long pause. On
        // resume, refresh immediately if it's expired/near-expired so the first call after resume doesn't 401.
        private void OnApplicationPause(bool paused)
        {
            if (paused || !IsReady || _refreshing) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_authSave.ExpiresAtUnix - tokenRefreshSkewSeconds <= now)
                _ = ProactiveRefreshAsync();
        }

        private async Task ProactiveRefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try { await RefreshTokenAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[AccountManager] proactive token refresh failed: {ex.Message}"); }
            finally { _refreshing = false; }
        }

        private async Task InitializeAsync()
        {
            // The cached token/UserId/DeviceId belong to ONE server+DB. If the API URL changed since they were
            // issued, drop them BEFORE anything reads them (RegisterDeviceAsync below sends UserId) so we
            // re-authenticate cleanly against the current server instead of flashing a stale account.
            InvalidateAuthIfServerChanged();

            // Optional: register device fingerprint on the server before account operations
            await RegisterDeviceAsync();

            if (TokenIsValid())
            {
                KhelaAnalytics.SetUserId(_authSave.UserId);
                KhelaAnalytics.LogLogin("cached_device_guest");
                IsReady = true;
                OnReady?.Invoke();
                return;
            }

            var ensured = await EnsureAccountAsync();
            if (!ensured)
            {
                Debug.LogError("[AccountManager] Failed to ensure account after retries.");
                return;
            }

            IsReady = true;
            // Re-arm the reboot guard. It is a latch, and nothing else clears it — so after ONE abandoned session the
            // "restart from Boot" recovery silently no-ops for the rest of the process, leaving a dead token with no
            // way back. We have a live session here, so the previous reboot is done with.
            _rebooting = false;
            OnReady?.Invoke();
        }

        /// <summary>
        /// Guarantees a usable token BEFORE a request goes out: refreshes now if the cached one is missing, expired,
        /// or within the refresh skew of expiring. Cheap when the token is healthy (no network, just a clock compare).
        ///
        /// This is the proactive half of auth recovery — reacting to a 401 afterwards works, but only after the user
        /// has already seen a failed screen. Shares the single-flight attempt, so a burst of requests refreshes once.
        /// </summary>
        public Task<bool> EnsureValidTokenAsync()
        {
            if (TokenIsValid()) return Task.FromResult(true);
            return HandleAuthFailureAsync();
        }

        private bool TokenIsValid()
        {
            if (string.IsNullOrEmpty(_authSave.Token)) return false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return _authSave.ExpiresAtUnix - tokenRefreshSkewSeconds > now;
        }

        /// <summary>
        /// Ensures we have a registered user and a valid token.
        /// </summary>
        private async Task<bool> EnsureAccountAsync()
        {
            // Regenerate if ANY of the three is missing — not just Email/Password. A stale/partial save with an Email
            // + Password but a blank Username slips past a two-field check and then register fails with
            // "Username '' is invalid". GenerateDeviceUser derives all three consistently from the device id.
            if (string.IsNullOrEmpty(_authSave.Email)
                || string.IsNullOrEmpty(_authSave.Username)
                || string.IsNullOrEmpty(_authSave.Password))
            {
                GenerateCredentialsFromDevice();
            }

            // Try login first if credentials exist
            if (await TryLoginAsync())
            {
                return true;
            }

            // Try register (with one retry)
            for (int attempt = 0; attempt <= signupRetryCount; attempt++)
            {
                if (await TryRegisterAsync())
                {
                    return true;
                }
            }

            return false;
        }

        private void GenerateCredentialsFromDevice()
        {
            var (email, username, password) = AuthHelpers.GenerateDeviceUser(GetDeviceId());
            _authSave.Email = email;
            _authSave.Username = username;
            _authSave.Password = password;
            SaveController.MarkDirty();
            SaveController.Save();
        }

        private async Task<bool> TryRegisterAsync()
        {
            var request = new RegisterRequest
            {
                Email = _authSave.Email,
                Username = _authSave.Username,
                Password = _authSave.Password,
                CountryCode = GetCountryCode(),
                DeviceId = _authSave.DeviceId
            };

            var response = await PostJsonAsync<AuthResponse>(registerEndpoint, request);
            if (response == null) return false;

            CacheAuth(response);
            KhelaAnalytics.LogLogin("register_device_guest");
            return true;
        }

        private async Task<bool> TryLoginAsync(bool logAnalytics = true)
        {
            var request = new LoginRequest
            {
                Email = _authSave.Email,
                Password = _authSave.Password,
                DeviceId = _authSave.DeviceId
            };

            var response = await PostJsonAsync<AuthResponse>(loginEndpoint, request);
            if (response == null) return false;

            CacheAuth(response);
            if (logAnalytics)
                KhelaAnalytics.LogLogin("login_device_guest");
            return true;
        }

        public async Task<bool> RefreshTokenAsync()
        {
            var success = await TryLoginAsync(logAnalytics: false);
            if (success)
            {
                OnTokenRefreshed?.Invoke();
            }
            return success;
        }

        private void CacheAuth(AuthResponse auth)
        {
            _authSave.Token = auth.Token;
            _authSave.Username = auth.Username;
            _authSave.UserId = auth.UserId;
            _authSave.ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + auth.ExpiresIn;
            _authSave.ServerUrl = SafeBaseUrl();   // bind the token to its issuer so a later URL switch invalidates it
            SaveController.MarkDirty();
            SaveController.Save();
            KhelaAnalytics.SetUserId(_authSave.UserId);
        }

        /// <summary>
        /// Adopts the token returned by an external/social sign-in (see KhelaAuthService). The server has
        /// already resolved which account this identity belongs to — including upgrading this device's guest
        /// account in place — so we simply cache the new token and carry on as that user.
        /// </summary>
        public void ApplyExternalAuth(AuthResponse auth, string analyticsMethod)
        {
            if (auth == null || string.IsNullOrEmpty(auth.Token)) return;

            CacheAuth(auth);
            KhelaAnalytics.LogLogin(string.IsNullOrEmpty(analyticsMethod) ? "social" : analyticsMethod);

            IsReady = true;
            OnTokenRefreshed?.Invoke();
        }

        // In-flight re-auth, shared by every caller. A token expiring mid-session 401s EVERY request that happens to
        // be in the air at once (board poll, wallet, lobby, heartbeat...). Without this each one would start its own
        // login: several concurrent logins against the same device account, each overwriting the other's token, and
        // whichever finishes last wins — so a request could be replayed with a token that has already been replaced.
        private Task<bool> _reauth;

        /// <summary>
        /// Call when a request fails with 401/expired: re-authenticates ONCE and reports whether the caller may
        /// replay. Concurrent callers all await the SAME attempt rather than racing their own.
        /// </summary>
        public Task<bool> HandleAuthFailureAsync()
        {
            // Piggy-back on an attempt already running (its result is exactly what we'd compute).
            if (_reauth != null && !_reauth.IsCompleted) return _reauth;

            _reauth = ReauthenticateAsync();
            return _reauth;
        }

        private async Task<bool> ReauthenticateAsync()
        {
            try
            {
                if (await RefreshTokenAsync()) return true;

                // Refresh failed — fall back to a full ensure (re-register / re-login). Device credentials are derived
                // deterministically from the device id, so this recovers even from a wiped local save.
                if (await EnsureAccountAsync()) return true;

                // Everything failed: the session is genuinely dead and every screen from here would only render
                // errors ("Couldn't load tables: HTTP 401"). Restart the whole flow from Boot, which logs in again.
                RestartFromBoot();
                return false;
            }
            finally
            {
                _reauth = null;   // let the NEXT failure start a fresh attempt
            }
        }

        /// <summary>
        /// Give up on this session and restart from Boot. Call when a 401 has survived recovery — a refresh, a
        /// re-login and a replay — because at that point no screen can render anything but errors. Safe to call from
        /// several failing requests at once; only the first takes effect.
        /// </summary>
        public void AbandonSession() => RestartFromBoot();

        // Guarded so a burst of simultaneous 401s (board poll + wallet + lobby + heartbeat all failing at once)
        // triggers ONE reboot rather than a scene load per failed request.
        private bool _rebooting;

        private void RestartFromBoot()
        {
            if (_rebooting) return;
            _rebooting = true;
            Debug.LogWarning("[AccountManager] re-authentication failed — restarting from Boot to log in again.");
            SceneNavigator.GoToBoot();
        }

        private async Task RegisterDeviceAsync()
        {
            var payload = new DeviceRegisterRequest
            {
                Fingerprint = ComputeFingerprint(),
                AppSetId = GetAppSetId(),
                GameVersion = Application.version,
                UserId = _authSave.UserId,
                TimeZone = TimeZoneInfo.Local.Id
            };

            var response = await PostJsonAsync<DeviceRegisterResponse>(deviceRegisterEndpoint, payload);
            if (response != null && !string.IsNullOrEmpty(response.DeviceId))
            {
                _authSave.DeviceId = response.DeviceId;
                SaveController.MarkDirty();
                SaveController.Save();
            }
        }

        private string ComputeFingerprint()
        {
            var pieces = new List<string>
            {
                SystemInfo.deviceModel,
                SystemInfo.graphicsDeviceName,
                SystemInfo.graphicsDeviceType.ToString(),
                SystemInfo.processorType,
                SystemInfo.processorCount.ToString(),
                Screen.currentResolution.ToString(),
                Application.systemLanguage.ToString(),
                TimeZoneInfo.Local.StandardName,
                SystemInfo.operatingSystem
            };

            var input = string.Join("|", pieces);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.AppendFormat("{0:x2}", b);
            }
            return sb.ToString();
        }

        private string GetAppSetId()
        {
            // Intentionally empty. Reading a real App Set ID needs the play-services-appset dependency AND an
            // async callback — and the result was always discarded here anyway, so the native call only ever
            // produced a ClassNotFoundException warning. Device registration uses ComputeFingerprint() instead.
            // Left as a seam if we ever add a proper (async) App Set ID later.
            return string.Empty;
        }

        private string GetDeviceId()
        {
            // Best-effort stable ID; avoid advertising IDs. Unity's deviceUniqueIdentifier may reset on reinstall.
            return SystemInfo.deviceUniqueIdentifier;
        }

        // Prefer an explicit, non-placeholder inspector URL; otherwise use the shared AppConfig
        // so there's a single server URL across auth, the REST client, and the hub.
        private string ResolveBaseUrl()
            => !string.IsNullOrWhiteSpace(baseApiUrl) && !baseApiUrl.Contains("example.com")
                ? baseApiUrl.TrimEnd('/')
                : AppConfig.Instance.BaseApiUrl;

        /// <summary>ResolveBaseUrl that never throws — AppConfig.Instance may be null at the very first boot moment.</summary>
        private string SafeBaseUrl()
        {
            try { return ResolveBaseUrl(); } catch { return string.Empty; }
        }

        /// <summary>
        /// The cached token + UserId + DeviceId belong to the server that ISSUED them: a JWT is only valid on
        /// its issuer, and the device-guest account is per-DB. If the configured API URL has changed since —
        /// e.g. dev switching localhost &lt;-&gt; live — trusting the old token makes the client briefly authenticate
        /// as a stale/absent account (no avatar =&gt; Onboarding) until a 401 forces a re-login, which is the
        /// "onboarding, then the old profile snaps back" flip. So on a server change we discard ONLY the
        /// server-specific bits; the deterministic device-guest login (Email/Password derived from the device
        /// id — identical on every server) then re-resolves the correct account for the current server.
        /// </summary>
        private void InvalidateAuthIfServerChanged()
        {
            var current = SafeBaseUrl();
            if (string.IsNullOrEmpty(current)) return;          // config not ready; 401->refresh is the backstop
            if (string.IsNullOrEmpty(_authSave.Token)) return;  // nothing cached to invalidate

            if (!string.Equals(_authSave.ServerUrl, current, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log($"[AccountManager] API server changed ('{_authSave.ServerUrl}' -> '{current}'); " +
                          "discarding cross-server token/account and re-authenticating for this server.");
                _authSave.Token = string.Empty;
                _authSave.UserId = string.Empty;
                _authSave.ExpiresAtUnix = 0;
                _authSave.DeviceId = string.Empty;   // device rows are per-server DB; RegisterDeviceAsync re-issues one
                // Email/Password kept on purpose — deterministic from the device id, so the same account on every server.
                SaveController.MarkDirty();
                SaveController.Save();
            }
        }

        /// <summary>
        /// Shared JSON POST. Pass <paramref name="bearerToken"/> to authenticate the call as the current
        /// user — required by the social sign-in upgrade path, where the server links the new identity onto
        /// the account the caller is already signed into.
        /// </summary>
        public async Task<T> PostJsonAsync<T>(string endpoint, object payload, bool expectResponseBody = true, string bearerToken = null) where T : class
        {
            var url = $"{ResolveBaseUrl()}{endpoint}";
            try
            {
                var json = JsonSerializer.Serialize(payload, JsonOpts);
                var req = new HTTPRequest(new Uri(url), HTTPMethods.Post);
                // MUST have a timeout. This is the transport for the token REFRESH, and every game request awaits
                // EnsureValidTokenAsync before it sends — so a refresh with no deadline stalls the whole REST channel
                // (deal, repeat, hand log) while SignalR carries on updating the board. Same budget as
                // BlackjackRestClient.SendRawAsync so auth can never outlast an ordinary request.
                req.TimeoutSettings.Timeout = TimeSpan.FromSeconds(Mathf.Max(1, AppConfig.Instance.RequestTimeoutSeconds));
                req.SetHeader("Content-Type", "application/json; charset=utf-8");
                if (!string.IsNullOrEmpty(bearerToken))
                    req.SetHeader("Authorization", $"Bearer {bearerToken}");
                req.UploadSettings.UploadStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

                var response = await req.GetHTTPResponseAsync();
                // Best HTTP does NOT throw on non-2xx — it returns the 4xx/5xx response (same as BlackjackRestClient).
                // We MUST check the status: otherwise a 401/400 error body gets deserialized into a non-null empty
                // object, and the caller (TryLogin/TryRegister) treats the failure as SUCCESS — caching an empty token,
                // skipping the register fallback, and 401-ing every later call with no error logged.
                if (response == null || response.StatusCode < 200 || response.StatusCode >= 300)
                {
                    Debug.LogWarning($"[AccountManager] POST {url} → {response?.StatusCode}: {response?.DataAsText}");
                    return null;
                }
                if (!expectResponseBody)
                    return Activator.CreateInstance<T>();
                return JsonSerializer.Deserialize<T>(response.DataAsText, JsonOpts);
            }
            catch (AsyncHTTPException hex)
            {
                Debug.LogWarning($"[AccountManager] POST {url} failed: {hex.StatusCode} — {hex.Content}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AccountManager] POST failed: {ex}");
                return null;
            }
        }

        public string GetCountryCode()
        {
            try 
            {
                var culture = System.Globalization.CultureInfo.CurrentCulture;
                var region = new System.Globalization.RegionInfo(culture.Name);
                return region.TwoLetterISORegionName.ToLowerInvariant();
            }
            catch
            {
                return "bd";
            }
        }

        private void OnDestroy()
        {
            SaveController.Save(true);
        }
    }

    /// <summary>
    /// Local auth save payload.
    /// </summary>
    [Serializable]
    public class AuthSave : ISaveObject
    {
        public string Email;
        public string Username;
        public string Password;
        public string Token;
        public string UserId;
        public long ExpiresAtUnix;
        public string DeviceId;
        public string ServerUrl;   // base API URL that issued Token/UserId/DeviceId; a mismatch invalidates them

        public string Key => "auth";

        public string SaveToJson() => JsonUtility.ToJson(this);

        public void LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            JsonUtility.FromJsonOverwrite(json, this);
        }
    }
}
