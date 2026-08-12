using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PlayCard.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayCard.App
{
    /// <summary>
    /// GLOBAL log recorder — Development builds and the Editor always; a RELEASE build too when
    /// <see cref="AppConfig.RecordLogsInReleaseBuilds"/> is ticked on the AppConfig asset in Resources.
    /// Auto-starts at app launch via <see cref="RuntimeInitializeOnLoadMethod"/>
    /// — no scene setup, no GameObject to place — and captures every Unity log/exception (from ANY thread) into a
    /// per-session file under <c>persistentDataPath/khela_logs/</c> with AutoFlush (survives a crash). Because it
    /// starts before the first scene, it catches boot/auth failures that only happen on-device.
    ///
    /// The UI (Send / Clear buttons + status) lives in whatever scene you want, wired by <see cref="DevLogBinder"/> —
    /// this class owns the recording + upload; the binder just calls <see cref="SendLogs"/> / <see cref="ClearOldLogs"/>
    /// and reads <see cref="Status"/> / <see cref="OnStatus"/>. In a Release build with the flag off it never
    /// instantiates (zero cost).
    /// </summary>
    public sealed class DevLogRecorder : MonoBehaviour
    {
        // Upload target + gate. DevKey MUST match the server's DevLog:Key. Consts because the recorder is created at
        // runtime (no Inspector) — this is a dev tool, so a baked-in key is acceptable.
        private const string UploadEndpoint = "/api/devlog/upload";
        private const string DevKey = "khela-devlog-6f3a92";

        /// <summary>
        /// Hard cap on the upload body. The payload used to be every session log ever recorded, concatenated with no
        /// bound — it grew with the device's lifetime and eventually every Send failed.
        ///
        /// The number is set by the SMALLEST limit in the chain. Two apply, and they are NOT the same:
        ///   • nginx <c>client_max_body_size</c> — now <c>5m</c> in sites-enabled/khela. It rejects with a 413 BEFORE
        ///     the request reaches Kestrel, so the server logs NOTHING and the failure is invisible from that side.
        ///     Its default is 1 MB, which is what silently broke every upload until Aug-2026.
        ///   • the controller's own <c>RequestSizeLimit</c> — 5 MB.
        /// 4.5 MB leaves headroom under both rather than sitting exactly on the limit. Raising this past 5 MB requires
        /// BOTH the nginx directive and <c>DevLogController.MaxBytes</c> to move first.
        /// </summary>
        private const int MaxUploadBytes = 4500 * 1024;
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

        /// <summary>
        /// True when this build should record: always in the Editor and Development builds, and in a RELEASE build
        /// when <see cref="AppConfig.RecordLogsInReleaseBuilds"/> is ticked.
        ///
        /// The switch lives on the AppConfig asset rather than on <see cref="DevLogBinder"/> because this runs
        /// BEFORE the first scene — the binder sits in Home, which loads well after boot, so a flag on it could
        /// never enable the boot logging that matters most on device. AppConfig is in Resources, so it loads here.
        /// </summary>
        private static bool ShouldRecord()
        {
            if (Debug.isDebugBuild) return true;
            try { return AppConfig.Instance != null && AppConfig.Instance.RecordLogsInReleaseBuilds; }
            catch { return false; }   // never let a missing/broken config stop the app from starting
        }

        // Self-instantiate before the first scene loads, so recording covers app start.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (!ShouldRecord()) return;
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

        /// <summary>
        /// Build the upload body, NEWEST session first and bounded by <see cref="MaxUploadBytes"/>.
        ///
        /// Newest-first matters twice: it is the session you are actually debugging, and it means the budget is spent
        /// on recent logs rather than exhausted by ancient ones. If a single session is itself over budget we keep its
        /// TAIL — a crash is at the end of a log, never the beginning.
        /// </summary>
        private string CollectAllLogs()
        {
            var sb = new StringBuilder();
            int budget = MaxUploadBytes;
            int skipped = 0;

            var files = new List<string>(Directory.GetFiles(_dir, "*.log"));
            // Newest first. The current session's file sorts last by name, so sort by write time, not by name.
            files.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));

            foreach (var f in files)
            {
                if (budget <= 0) { skipped++; continue; }

                string text;
                try { text = File.ReadAllText(f); }
                catch (Exception ex) { text = $"<read failed: {ex.Message}>\n"; }

                bool trimmed = false;
                if (text.Length > budget)
                {
                    text = text.Substring(text.Length - budget);   // keep the END — that's where the failure is
                    trimmed = true;
                }

                sb.Append("\n\n===== ").Append(Path.GetFileName(f));
                if (trimmed) sb.Append("  [TRUNCATED — tail only]");
                sb.Append(" =====\n").Append(text);
                budget -= text.Length;
            }

            if (skipped > 0)
                sb.Append("\n\n===== ").Append(skipped).Append(" older session(s) omitted (upload size cap) =====\n");

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
