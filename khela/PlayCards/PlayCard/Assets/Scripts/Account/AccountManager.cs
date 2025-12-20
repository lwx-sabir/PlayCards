using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks; 
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

        public event Action OnReady;
        public event Action OnTokenRefreshed;

        private readonly HttpClient _httpClient = new HttpClient();
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

            // Fire-and-forget async init
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            // Optional: register device fingerprint on the server before account operations
            await RegisterDeviceAsync();

            if (TokenIsValid())
            {
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
            return true;
        }

        private async Task<bool> TryLoginAsync()
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
            return true;
        }

        public async Task<bool> RefreshTokenAsync()
        {
            var success = await TryLoginAsync();
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

        private async Task<T?> PostJsonAsync<T>(string endpoint, object payload, bool expectResponseBody = true) where T : class
        {
            try
            {
                var url = $"{baseApiUrl}{endpoint}";
                var json = JsonUtility.ToJson(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.LogWarning($"[AccountManager] POST {url} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    return null;
                }

                if (!expectResponseBody)
                    return Activator.CreateInstance<T>();

                var body = await response.Content.ReadAsStringAsync();
                return JsonUtility.FromJson<T>(body);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AccountManager] POST failed: {ex}");
                return null;
            }
        }

        private string GetCountryCode()
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
            _httpClient.Dispose();
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
