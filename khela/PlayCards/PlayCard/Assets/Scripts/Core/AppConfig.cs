using UnityEngine;

namespace PlayCard.Core
{
    /// <summary>
    /// Single source of truth for server connection settings. One asset
    /// (a <c>Resources/AppConfig</c>) feeds the REST client and the SignalR hub client so
    /// the base URL can never drift between them.
    ///
    /// Create via <b>Assets ▸ Create ▸ PlayCard ▸ App Config</b> and put it in any
    /// <c>Resources/</c> folder. If no asset exists, <see cref="Instance"/> falls back to
    /// localhost defaults so play-mode still runs against a local backend.
    /// </summary>
    [CreateAssetMenu(fileName = "AppConfig", menuName = "PlayCard/App Config", order = 0)]
    public sealed class AppConfig : ScriptableObject
    {
        [Header("Server")]
        [Tooltip("Backend base URL, no trailing slash. e.g. http://localhost:5044 (dev) or https://api.khela.game")]
        [SerializeField] private string baseApiUrl = "http://localhost:5044";

        [Tooltip("SignalR hub path appended to the base URL — or a full http(s) URL to override it entirely.")]
        [SerializeField] private string hubPath = "/blackjackhub";

        [Header("HTTP")]
        [Tooltip("Per-request timeout in seconds for REST calls.")]
        [SerializeField] private int requestTimeoutSeconds = 20;

        /// <summary>Base URL with any trailing slash stripped.</summary>
        public string BaseApiUrl => string.IsNullOrWhiteSpace(baseApiUrl) ? "http://localhost:5044" : baseApiUrl.TrimEnd('/');

        /// <summary>Full SignalR hub URL (absolute if <c>hubPath</c> is a full URL, else base + path).</summary>
        public string HubUrl => hubPath != null && hubPath.StartsWith("http") ? hubPath : BaseApiUrl + hubPath;

        public int RequestTimeoutSeconds => requestTimeoutSeconds;

        private static AppConfig _instance;

        /// <summary>
        /// Loads the singleton config from <c>Resources/AppConfig</c>. Returns an in-memory
        /// default (localhost:5044) if the asset is missing, so nothing hard-crashes before
        /// the asset is authored.
        /// </summary>
        public static AppConfig Instance
        {
            get
            {
                if (_instance != null) return _instance;

                _instance = Resources.Load<AppConfig>("AppConfig");
                if (_instance == null)
                {
                    Debug.LogWarning("[AppConfig] No 'Resources/AppConfig' asset found — using localhost:5044 defaults. " +
                                     "Create one via Assets ▸ Create ▸ PlayCard ▸ App Config and place it under a Resources/ folder.");
                    _instance = CreateInstance<AppConfig>();
                }
                return _instance;
            }
        }
    }
}
