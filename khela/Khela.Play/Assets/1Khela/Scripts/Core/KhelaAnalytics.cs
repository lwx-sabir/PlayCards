using System;
using System.Collections;
using System.Collections.Generic;
using Firebase;
using Firebase.Analytics;
using Firebase.Crashlytics;
using UnityEngine;

namespace PlayCard.Core
{
    /// <summary>
    /// Central Firebase Analytics / Crashlytics wrapper for the Unity client.
    /// Game code should call this service instead of using Firebase APIs directly.
    /// </summary>
    public sealed class KhelaAnalytics : MonoBehaviour
    {
        public static KhelaAnalytics Instance { get; private set; }

        [Header("Lifecycle")]
        [SerializeField] private bool dontDestroyOnLoad = true;
        [SerializeField] private bool initializeOnAwake = true;
        [SerializeField] private bool enableInEditor = false;

        [Header("Collections")]
        [SerializeField] private bool enableAnalyticsCollection = true;
        [SerializeField] private bool enableCrashlyticsCollection = true;

        public bool IsReady { get; private set; }

        private const int MaxQueuedEvents = 64;

        private readonly List<QueuedEvent> _queuedEvents = new List<QueuedEvent>();
        private bool _initializationStarted;
        private string _pendingUserId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
#if !UNITY_SERVER
            if (Instance != null) return;

            var go = new GameObject("[KhelaAnalytics]");
            go.AddComponent<KhelaAnalytics>();
#endif
        }

        private void Awake()
        {
            if (!CanUseFirebaseRuntime())
            {
                Destroy(gameObject);
                return;
            }

            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            if (dontDestroyOnLoad)
                DontDestroyOnLoad(gameObject);

            if (initializeOnAwake)
                Initialize();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void Initialize()
        {
            if (!CanUseFirebaseRuntime() || IsReady || _initializationStarted)
                return;

            _initializationStarted = true;
            StartCoroutine(InitializeFirebaseCo());
        }

        private IEnumerator InitializeFirebaseCo()
        {
            var dependencyTask = FirebaseApp.CheckAndFixDependenciesAsync();
            while (!dependencyTask.IsCompleted)
                yield return null;

            if (dependencyTask.IsFaulted)
            {
                Debug.LogError($"KhelaAnalytics: Firebase dependency check failed: {dependencyTask.Exception}");
                yield break;
            }

            if (dependencyTask.Result != DependencyStatus.Available)
            {
                Debug.LogError($"KhelaAnalytics: Firebase dependencies unavailable: {dependencyTask.Result}");
                yield break;
            }

            _ = FirebaseApp.DefaultInstance;

            FirebaseAnalytics.SetAnalyticsCollectionEnabled(enableAnalyticsCollection);
            FirebaseAnalytics.SetSessionTimeoutDuration(TimeSpan.FromMinutes(30));

            Crashlytics.ReportUncaughtExceptionsAsFatal = true;
            Crashlytics.IsCrashlyticsCollectionEnabled = enableCrashlyticsCollection;

            IsReady = true;

            if (!string.IsNullOrWhiteSpace(_pendingUserId))
                ApplyUserId(_pendingUserId);

            LogEventInternal("analytics_initialized",
                new Parameter("platform", Application.platform.ToString()),
                new Parameter("app_version", Application.version));

            FlushQueuedEvents();
        }

        public static void SetUserId(string userId)
        {
            if (Instance == null || string.IsNullOrWhiteSpace(userId) || !Instance.CanUseFirebaseRuntime()) return;
            if (!Instance.IsReady)
            {
                Instance._pendingUserId = Trim(userId, 100);
                return;
            }

            ApplyUserId(userId);
        }

        public static void SetUserProperty(string key, string value)
        {
            if (!Ready || string.IsNullOrWhiteSpace(key)) return;
            FirebaseAnalytics.SetUserProperty(Trim(key, 24), Trim(value, 36));
        }

        public static void LogAppOpen()
        {
            LogEvent("app_open_khela",
                new Parameter("platform", Application.platform.ToString()),
                new Parameter("app_version", Application.version));
        }

        public static void LogScreen(string screenName)
        {
            if (string.IsNullOrWhiteSpace(screenName)) return;

            var screen = Trim(screenName, 100);
            LogEvent(FirebaseAnalytics.EventScreenView,
                new Parameter(FirebaseAnalytics.ParameterScreenName, screen),
                new Parameter(FirebaseAnalytics.ParameterScreenClass, screen));
        }

        public static void LogLogin(string method)
        {
            LogEvent(FirebaseAnalytics.EventLogin,
                new Parameter(FirebaseAnalytics.ParameterMethod, Trim(method, 36)));
        }

        public static void LogGameSelected(string gameKey)
        {
            LogEvent("game_selected",
                new Parameter("game_key", Trim(gameKey, 40)));
        }

        public static void LogLobbyOpened(string gameKey)
        {
            LogEvent("lobby_opened",
                new Parameter("game_key", Trim(gameKey, 40)));
        }

        public static void LogTableJoined(string gameKey, string tableId, int seatNumber)
        {
            LogEvent("table_joined",
                new Parameter("game_key", Trim(gameKey, 40)),
                new Parameter("table_id", Trim(tableId, 64)),
                new Parameter("seat_number", seatNumber));
        }

        public static void LogBetPlaced(string gameKey, decimal amount, int seatNumber)
        {
            LogEvent("bet_placed",
                new Parameter("game_key", Trim(gameKey, 40)),
                new Parameter("amount_chips", (double)amount),
                new Parameter("seat_number", seatNumber));
        }

        public static void LogRoundStarted(string gameKey, string tableId)
        {
            LogEvent("round_started",
                new Parameter("game_key", Trim(gameKey, 40)),
                new Parameter("table_id", Trim(tableId, 64)));
        }

        public static void LogRoundEnded(string gameKey, string tableId, string outcome, decimal netDelta, decimal payout)
        {
            LogEvent("round_ended",
                new Parameter("game_key", Trim(gameKey, 40)),
                new Parameter("table_id", Trim(tableId, 64)),
                new Parameter("outcome", Trim(outcome, 24)),
                new Parameter("net_delta_chips", (double)netDelta),
                new Parameter("payout_chips", (double)payout));
        }

        public static void LogPurchaseStarted(string productId)
        {
            LogEvent("purchase_started",
                new Parameter("product_id", Trim(productId, 100)));
        }

        public static void LogPurchaseCompleted(string productId, string currency, double value)
        {
            LogEvent(FirebaseAnalytics.EventPurchase,
                new Parameter("item_id", Trim(productId, 100)),
                new Parameter("currency", Trim(currency, 3)),
                new Parameter("value", value));
        }

        public static void LogNonFatal(string message)
        {
            if (!Ready || string.IsNullOrWhiteSpace(message)) return;
            Crashlytics.Log(Trim(message, 1000));
        }

        public static void LogException(Exception ex)
        {
            if (!Ready || ex == null) return;
            Crashlytics.LogException(ex);
        }

        private static void LogEvent(string eventName, params Parameter[] parameters)
        {
            if (Instance == null || string.IsNullOrWhiteSpace(eventName) || !Instance.CanUseFirebaseRuntime()) return;

            if (!Instance.IsReady)
            {
                Instance.QueueEvent(eventName, parameters);
                return;
            }

            LogEventInternal(eventName, parameters);
        }

        private static void LogEventInternal(string eventName, params Parameter[] parameters)
        {
            FirebaseAnalytics.LogEvent(Trim(eventName, 40), parameters);
        }

        private void QueueEvent(string eventName, Parameter[] parameters)
        {
            if (_queuedEvents.Count >= MaxQueuedEvents)
                _queuedEvents.RemoveAt(0);

            _queuedEvents.Add(new QueuedEvent
            {
                Name = Trim(eventName, 40),
                Parameters = parameters ?? Array.Empty<Parameter>()
            });
        }

        private void FlushQueuedEvents()
        {
            foreach (var queued in _queuedEvents)
                LogEventInternal(queued.Name, queued.Parameters);

            _queuedEvents.Clear();
        }

        private static void ApplyUserId(string userId)
        {
            userId = Trim(userId, 100);
            FirebaseAnalytics.SetUserId(userId);
            Crashlytics.SetUserId(userId);
        }

        private static bool Ready => Instance != null && Instance.IsReady && Instance.CanUseFirebaseRuntime();

        private bool CanUseFirebaseRuntime()
        {
            if (Application.isBatchMode) return false;

#if UNITY_SERVER
            return false;
#elif UNITY_EDITOR
            return enableInEditor;
#elif UNITY_ANDROID || UNITY_IOS
            return true;
#else
            return false;
#endif
        }

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private sealed class QueuedEvent
        {
            public string Name;
            public Parameter[] Parameters;
        }
    }
}
