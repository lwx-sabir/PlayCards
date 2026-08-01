using System;
using System.Text;
using System.Text.RegularExpressions;
using PlayCard.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Editor tool — <b>Khela ▸ Create Admin If Not Exist</b>. Enter an email + password and it creates that login on
    /// the server IF it doesn't already exist, then shows the account's user Id (also copied to the clipboard).
    /// Idempotent: it logs in first to check, so re-running never makes a duplicate.
    ///
    /// One honest caveat: on this backend "admin" is granted by CONFIG, not a DB flag — the account's Id has to be in
    /// <c>Admin:UserIds</c> in the SERVER's appsettings.json (then a restart). A client-side editor tool can't edit
    /// server config, so this tool creates the account and hands you the Id + the exact snippet to paste server-side.
    /// </summary>
    public sealed class AdminAccountTool : EditorWindow
    {
        private const string ApiPref = "Khela.AdminTool.ApiUrl";

        private string _apiUrl = "", _email = "", _username = "", _password = "";
        private string _log = "";
        private Vector2 _scroll;

        [MenuItem("Khela/Create Admin If Not Exist")]
        private static void Open()
        {
            var w = GetWindow<AdminAccountTool>(true, "Create Admin");
            w.minSize = new Vector2(500, 360);
        }

        private void OnEnable()
        {
            _apiUrl = EditorPrefs.GetString(ApiPref, "");
            if (string.IsNullOrWhiteSpace(_apiUrl))
                _apiUrl = AppConfig.Instance != null ? AppConfig.Instance.BaseApiUrl : "http://localhost:5044";
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Creates the login if it doesn't exist, then shows its user Id (copied to clipboard).\n\n" +
                "To actually GRANT admin: add that Id to \"Admin:UserIds\" in the SERVER's appsettings.json and restart " +
                "the service. Admin here is config-based, so a client tool can't flip it.", MessageType.Info);

            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _apiUrl = EditorGUILayout.TextField("API base URL", _apiUrl);
                if (GUILayout.Button("From AppConfig", GUILayout.Width(120)))
                    _apiUrl = AppConfig.Instance != null ? AppConfig.Instance.BaseApiUrl : _apiUrl;
            }
            EditorPrefs.SetString(ApiPref, _apiUrl);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Account", EditorStyles.boldLabel);
            _email = EditorGUILayout.TextField("Email", _email);
            _username = EditorGUILayout.TextField("Username (blank = from email)", _username);
            _password = EditorGUILayout.PasswordField("Password", _password);

            EditorGUILayout.Space();
            bool disabled = string.IsNullOrWhiteSpace(_apiUrl)
                            || string.IsNullOrWhiteSpace(_email)
                            || string.IsNullOrWhiteSpace(_password);
            using (new EditorGUI.DisabledScope(disabled))
            {
                if (GUILayout.Button("Create Admin If Not Exist", GUILayout.Height(32)))
                    CreateIfNotExist();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(130));
            EditorGUILayout.SelectableLabel(_log, EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private void CreateIfNotExist()
        {
            string email = _email.Trim();
            string username = string.IsNullOrWhiteSpace(_username) ? email.Split('@')[0] : _username.Trim();

            // 1) Already there? A successful login proves the account exists AND the password matches.
            var (lc, lb) = Post("/api/auth/login", Json("email", email, "password", _password));
            if (lc >= 200 && lc < 300) { Done("already existed (login OK)", Field(lb, "userId")); return; }
            if (lc == 0)
            {
                _log = $"✗ Couldn't reach {_apiUrl} (status 0). Check the API base URL and that the server is running.";
                return;
            }

            // 2) Not logged in → create it.
            var (rc, rb) = Post("/api/auth/register", Json("email", email, "username", username, "password", _password));
            if (rc >= 200 && rc < 300) { Done("created", Field(rb, "userId")); return; }

            if ((rb ?? "").IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _log = $"✗ An account with that email already exists, but login failed ({lc}) — the password doesn't " +
                       "match. Enter the correct password (or reset it) and run again to fetch its Id.";
                return;
            }
            _log = $"✗ Create failed (register {rc}): {rb}\n\n(login first returned {lc}: {lb})";
        }

        private void Done(string what, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                _log = $"✓ Account {what}, but the response carried no userId — check the server response.";
                return;
            }
            EditorGUIUtility.systemCopyBuffer = id;
            _log =
                $"✓ Account {what}.\n\nUser Id (copied to clipboard):\n{id}\n\n" +
                "── To grant admin, add this to the SERVER's appsettings.json and restart the service ──\n" +
                $"\"Admin\": {{ \"UserIds\": [ \"{id}\" ] }}\n";
            Debug.Log($"[AdminAccountTool] {what}: {id}");
        }

        // ---------- tiny synchronous HTTP (same wait pattern as CosmeticExporter) ----------
        private (long code, string body) Post(string route, string json)
        {
            using var req = new UnityWebRequest(_apiUrl.TrimEnd('/') + route, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            var op = req.SendWebRequest();
            double start = EditorApplication.timeSinceStartup;
            while (!op.isDone && EditorApplication.timeSinceStartup - start < 30) System.Threading.Thread.Sleep(25);
            return (req.responseCode, req.downloadHandler != null ? (req.downloadHandler.text ?? req.error) : req.error);
        }

        private static string Json(params string[] kv)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(kv[i]).Append("\":\"").Append(Esc(kv[i + 1])).Append('"');
            }
            return sb.Append('}').ToString();
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Field(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
