using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Best.HTTP;
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

        private async Task InitializeAsync()
        {
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
            OnReady?.Invoke();
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
            if (string.IsNullOrEmpty(_authSave.Email) || string.IsNullOrEmpty(_authSave.Password))
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

        /// <summary>
        /// Should be called when a server request fails with 401/expired; attempts refresh then retries via the caller.
        /// </summary>
        public async Task<bool> HandleAuthFailureAsync()
        {
            if (await RefreshTokenAsync())
            {
                return true;
            }

            // If refresh fails, try full ensure (re-register/login)
            return await EnsureAccountAsync();
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
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                var task = new AndroidJavaClass("com.google.android.gms.appset.AppSet").CallStatic<AndroidJavaObject>("getClient", activity)
                    .Call<AndroidJavaObject>("getAppSetIdInfo");
                // This is asynchronous on Android; for brevity, return empty and rely on fingerprint.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AccountManager] AppSetId fetch failed: {ex.Message}");
            }
#endif
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
                req.SetHeader("Content-Type", "application/json; charset=utf-8");
                if (!string.IsNullOrEmpty(bearerToken))
                    req.SetHeader("Authorization", $"Bearer {bearerToken}");
                req.UploadSettings.UploadStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

                // Best HTTP throws AsyncHTTPException on any non-2xx (and on network/timeout); 2xx returns the response.
                var response = await req.GetHTTPResponseAsync();
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

        public string Key => "auth";

        public string SaveToJson() => JsonUtility.ToJson(this);

        public void LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            JsonUtility.FromJsonOverwrite(json, this);
        }
    }
}
