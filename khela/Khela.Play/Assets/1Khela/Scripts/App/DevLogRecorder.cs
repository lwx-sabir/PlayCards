using System;
using System.Collections;
using System.IO;
using System.Text;
using PlayCard.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayCard.App
{
    /// <summary>
    /// GLOBAL, dev-build-only log recorder. Auto-starts at app launch via <see cref="RuntimeInitializeOnLoadMethod"/>
    /// — no scene setup, no GameObject to place — and captures every Unity log/exception (from ANY thread) into a
    /// per-session file under <c>persistentDataPath/khela_logs/</c> with AutoFlush (survives a crash). Because it
    /// starts before the first scene, it catches boot/auth failures that only happen on-device.
    ///
    /// The UI (Send / Clear buttons + status) lives in whatever scene you want, wired by <see cref="DevLogBinder"/> —
    /// this class owns the recording + upload; the binder just calls <see cref="SendLogs"/> / <see cref="ClearOldLogs"/>
    /// and reads <see cref="Status"/> / <see cref="OnStatus"/>. In a Release build it never instantiates (zero cost).
    /// </summary>
    public sealed class DevLogRecorder : MonoBehaviour
    {
        // Upload target + gate. DevKey MUST match the server's DevLog:Key. Consts because the recorder is created at
        // runtime (no Inspector) — this is a dev tool, so a baked-in key is acceptable.
        private const string UploadEndpoint = "/api/devlog/upload";
        private const string DevKey = "khela-devlog-6f3a92";
        private const bool CaptureThreaded = true;   // capture off-thread logs too (async/HTTP errors fire off-thread)

        public static DevLogRecorder Instance { get; private set; }

        /// <summary>Latest status line (recording path, "Sent…", "Cleared…", errors). Binders display this.</summary>
        public string Status { get; private set; } = "";
        /// <summary>Fires whenever <see cref="Status"/> changes, so a scene binder can update its label live.</summary>
        public event Action<string> OnStatus;
        public bool IsSending { get; private set; }

        private string _dir;
        private string _sessionFile;
        private StreamWriter _writer;
        private readonly object _lock = new object();

        // Self-instantiate before the first scene loads (dev builds + Editor only), so recording covers app start.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (!Debug.isDebugBuild) return;           // Development Build or Editor only
            if (Instance != null) return;
            var go = new GameObject("[DevLogRecorder]");
            DontDestroyOnLoad(go);
            go.AddComponent<DevLogRecorder>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _dir = Path.Combine(Application.persistentDataPath, "khela_logs");
            try { Directory.CreateDirectory(_dir); } catch (Exception ex) { Debug.LogWarning($"[DevLogRecorder] mkdir failed: {ex.Message}"); }

            _sessionFile = Path.Combine(_dir, $"client_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            try
            {
                _writer = new StreamWriter(_sessionFile, append: true, Encoding.UTF8) { AutoFlush = true };
                WriteLine("INFO", $"=== session start {DateTime.Now:O} | {SystemInfo.deviceModel} | {Application.identifier} v{Application.version} | {SystemInfo.operatingSystem} | dev={Debug.isDebugBuild} ===", null);
            }
            catch (Exception ex) { Debug.LogWarning($"[DevLogRecorder] open log failed: {ex.Message}"); }

            if (CaptureThreaded) Application.logMessageReceivedThreaded += OnLog;
            else Application.logMessageReceived += OnLog;

            SetStatus($"Recording → {Path.GetFileName(_sessionFile)}");
        }

        private void OnDestroy()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceived -= OnLog;
            lock (_lock) { try { _writer?.Flush(); _writer?.Dispose(); } catch { } _writer = null; }
            if (Instance == this) Instance = null;
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            bool wantStack = type is LogType.Error or LogType.Exception or LogType.Assert;
            WriteLine(type.ToString(), condition, wantStack ? stackTrace : null);
        }

        private void WriteLine(string level, string msg, string stack)
        {
            if (_writer == null) return;
            lock (_lock)
            {
                try
                {
                    _writer.Write($"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}\n");
                    if (!string.IsNullOrEmpty(stack)) _writer.Write(stack + "\n");
                }
                catch { /* never let logging throw */ }
            }
        }

        // ---- public API (called by DevLogBinder's buttons) ----

        /// <summary>Upload ALL captured logs to the server (POST /api/devlog/upload → /var/khela/client_log/).</summary>
        public void SendLogs()
        {
            if (IsSending) return;
            StartCoroutine(SendRoutine());
        }

        /// <summary>Delete old session logs on the device; keeps the current session's file.</summary>
        public void ClearOldLogs()
        {
            int deleted = 0;
            try
            {
                foreach (var f in Directory.GetFiles(_dir, "*.log"))
                {
                    if (string.Equals(f, _sessionFile, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); deleted++; } catch { /* skip locked */ }
                }
            }
            catch (Exception ex) { SetStatus($"clear failed: {ex.Message}"); return; }
            SetStatus($"Cleared {deleted} old log file(s). Current session kept.");
        }

        private IEnumerator SendRoutine()
        {
            IsSending = true;
            SetStatus("Collecting logs…");
            lock (_lock) { try { _writer?.Flush(); } catch { } }

            string payload;
            try { payload = CollectAllLogs(); }
            catch (Exception ex) { SetStatus($"collect failed: {ex.Message}"); IsSending = false; yield break; }
            if (string.IsNullOrEmpty(payload)) { SetStatus("no logs to send"); IsSending = false; yield break; }

            var baseUrl = AppConfig.Instance != null ? AppConfig.Instance.BaseApiUrl : "";
            if (string.IsNullOrEmpty(baseUrl)) { SetStatus("no server URL (AppConfig)"); IsSending = false; yield break; }

            SetStatus($"Sending {payload.Length} bytes…");
            using var req = new UnityWebRequest(baseUrl.TrimEnd('/') + UploadEndpoint, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            req.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
            req.SetRequestHeader("X-Khela-DevKey", DevKey);
            req.SetRequestHeader("X-Device", Sanitize(SystemInfo.deviceModel));
            yield return req.SendWebRequest();

            bool ok = req.result == UnityWebRequest.Result.Success && req.responseCode >= 200 && req.responseCode < 300;
            SetStatus(ok ? $"Sent ✓ {req.downloadHandler.text}"
                         : $"Send failed ({req.responseCode}): {req.error} {req.downloadHandler?.text}");
            IsSending = false;
        }

        private string CollectAllLogs()
        {
            var sb = new StringBuilder();
            foreach (var f in Directory.GetFiles(_dir, "*.log"))
            {
                sb.Append("\n\n===== ").Append(Path.GetFileName(f)).Append(" =====\n");
                try { sb.Append(File.ReadAllText(f)); }
                catch (Exception ex) { sb.Append("<read failed: ").Append(ex.Message).Append(">\n"); }
            }
            return sb.ToString();
        }

        private void SetStatus(string s)
        {
            Status = s;
            OnStatus?.Invoke(s);
            Debug.Log($"[DevLogRecorder] {s}");   // also captured into the file
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "device";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            return sb.Length == 0 ? "device" : sb.ToString();
        }
    }
}
