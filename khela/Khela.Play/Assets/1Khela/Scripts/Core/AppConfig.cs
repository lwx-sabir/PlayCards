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
    [CreateAssetMenu(fileName = "AppConfig", menuName = "Khela/App Config", order = 0)]
    public sealed class AppConfig : ScriptableObject
    {
        [Header("Server")]
        [Tooltip("Backend base URL, no trailing slash. e.g. http://localhost:5044 (dev) or https://api.khela.game")]
        [SerializeField] private string baseApiUrl = "http://localhost:5044";

        [Tooltip("Blackjack SignalR hub path appended to the base URL — or a full http(s) URL to override it entirely.")]
        [SerializeField] private string hubPath = "/blackjackhub";

        [Tooltip("Three Card Poker SignalR hub path appended to the base URL — or a full http(s) URL to override it.")]
        [SerializeField] private string threeCardHubPath = "/threecardhub";

        [Header("HTTP")]
        [Tooltip("Per-request timeout in seconds for REST calls.")]
        [SerializeField] private int requestTimeoutSeconds = 20;

        [Header("Diagnostics")]
        [Tooltip("ON = the client log recorder runs in a RELEASE build too, exactly as it does in a Development " +
                 "build (records every log to a file and the debug panel's Send button uploads it). OFF (default) " +
                 "= Development builds and the Editor only. This lives here, not on DevLogBinder, because the " +
                 "recorder starts BEFORE the first scene — a flag on a scene object would miss the boot logs " +
                 "(auth, config, startup crashes), which are the ones worth having from a device.")]
        [SerializeField] private bool recordLogsInReleaseBuilds;

        /// <summary>Base URL with any trailing slash stripped.</summary>
        public string BaseApiUrl => string.IsNullOrWhiteSpace(baseApiUrl) ? "http://localhost:5044" : baseApiUrl.TrimEnd('/');

        /// <summary>Full blackjack SignalR hub URL (absolute if <c>hubPath</c> is a full URL, else base + path).</summary>
        public string HubUrl => HubUrlFor(hubPath, "/blackjackhub");

        /// <summary>Full Three Card Poker SignalR hub URL (absolute if the path is a full URL, else base + path).</summary>
        public string ThreeCardHubUrl => HubUrlFor(threeCardHubPath, "/threecardhub");

        public int RequestTimeoutSeconds => requestTimeoutSeconds;

        /// <summary>Let the log recorder run in a Release build (see the field tooltip).</summary>
        public bool RecordLogsInReleaseBuilds => recordLogsInReleaseBuilds;

        // Resolve a configured hub path against the base URL: a full http(s) value overrides entirely; an empty
        // value falls back to the game's default path. Shared by every per-game hub URL so they can't drift.
        private string HubUrlFor(string path, string fallback)
        {
            if (string.IsNullOrWhiteSpace(path)) path = fallback;
            return path.StartsWith("http") ? path : BaseApiUrl + path;
        }

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
