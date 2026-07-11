using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Bozo.ModularCharacters;
using PlayCard.Game.Net;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayCard.Avatar.EditorTools
{
    /// <summary>
    /// The cosmetics authoring tool (docs/AVATAR_SHOP_SPEC.md). Server-direct: each Save posts ONE SKU + its baked 3D
    /// icon straight into the DB (POST /api/shop/cosmetics/import for the row, POST /{id}/icon for the picture). There is
    /// NO local catalog file — the DB is the single source of truth, and the "On the server" list reads live from it.
    ///
    /// Workflow: Play the BoZo creator scene, dress + colour the rig, pick a piece, fill the fields, hit the green
    /// Save button. That's it — it's in the shop. Editor-only; the BoZo creator scene never ships.
    /// Menu: Khela ▸ Avatar ▸ Cosmetic Exporter.
    /// </summary>
    public sealed class CosmeticExporter : EditorWindow
    {
        private const string ApiPref = "Khela.CosmeticExporter.ApiUrl";
        private static readonly Regex IdRx = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$");

        /// <summary>Currencies a cosmetic may be priced in. Tokens is EXCLUDED BY CONSTRUCTION (guardrail) — the server rejects it too.</summary>
        private enum PriceCurrency { Coins = 0, Gems = 1, Kash = 2, Chips = 3 }
        private enum ColorMode { Fixed = 0, Palette = 1 }
        /// <summary>Who can wear it. Unisex serializes as "" (server treats empty as unisex).</summary>
        private enum SkuGender { Unisex = 0, Male = 1, Female = 2 }

        private OutfitSystem _rig;
        private int _mode;
        private static readonly string[] Modes = { "Item", "Set", "Character" };

        // commerce fields — everything the DB row stores
        private string _id = "", _name = "", _description = "";
        private SkuGender _skuGender = SkuGender.Unisex;
        private double _price;
        private PriceCurrency _currency = PriceCurrency.Kash;   // Kash = the cosmetics spend currency (default)
        private bool _isStarter, _exclusive;
        private bool _enabled = true;
        private int _sortOrder;

        // item mode
        private int _pieceIndex;
        private string _autoId = "";   // the id we last auto-filled — so a hand-typed id is never clobbered
        private ColorMode _colorMode = ColorMode.Fixed;
        private readonly List<Color> _palette = new List<Color>();

        // set mode
        private readonly List<bool> _ticks = new List<bool>();

        // character mode
        private AvatarConfig.Gender _gender = AvatarConfig.Gender.Male;
        private int _baseIndex;

        // server
        private string _apiUrl, _jwt = "";
        private List<ServerSku> _serverSkus = new List<ServerSku>();
        private string _serverErr;
        private bool _catalogLoaded;

        // icon booth
        private const int BoothLayer = 31;
        private const int IconSize = 1024;  // shop-grade resolution (server accepts ≤2MB PNG)
        private const int IconPadPx = 4;    // fixed margin (px per side) so every garment frames identically

        // live preview (re-baked automatically whenever the selection/colours change)
        private Texture2D _liveTex;
        private string _liveKey;

        private List<Outfit> _equipped = new List<Outfit>();
        private AvatarConfig _config;
        private string _log = "";
        private Vector2 _scroll;
        private bool _showContents = true;

        private static readonly Color OkColor = new Color(0.38f, 0.82f, 0.44f);
        private static readonly Color WarnColor = new Color(0.96f, 0.76f, 0.30f);

        [MenuItem("Khela/Avatar/Cosmetic Exporter")]
        public static void Open() => GetWindow<CosmeticExporter>("Cosmetic Exporter");

        private void OnEnable()
        {
            _apiUrl = EditorPrefs.GetString(ApiPref, "http://localhost:5044");
            if (_config == null)
            {
                var guid = AssetDatabase.FindAssets("t:AvatarConfig").FirstOrDefault();
                if (guid != null) _config = AssetDatabase.LoadAssetAtPath<AvatarConfig>(AssetDatabase.GUIDToAssetPath(guid));
            }
        }

        // ================================ GUI ================================

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (string.IsNullOrWhiteSpace(_jwt)) _jwt = PeekLocalToken();           // lazy auth (no network)
            if (!_catalogLoaded && !string.IsNullOrWhiteSpace(_jwt)) RefreshServerCatalog();   // one-time load

            DrawStatusStrip();
            EditorGUILayout.Space();

            _rig = (OutfitSystem)EditorGUILayout.ObjectField("Rig (OutfitSystem)", _rig, typeof(OutfitSystem), true);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Find rig in scene"))
            {
                // The creator scene holds several BSMC actors; the one being dressed is the ACTIVE one with the most outfits.
                _rig = FindObjectsByType<OutfitSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .OrderByDescending(os => os.gameObject.activeInHierarchy ? 1 : 0)
                    .ThenByDescending(os => { try { return os.GetOutfits()?.Count(o => o != null) ?? 0; } catch { return 0; } })
                    .FirstOrDefault();
                RefreshEquipped();
            }
            if (GUILayout.Button("Refresh equipped pieces")) RefreshEquipped();
            EditorGUILayout.EndHorizontal();

            DrawServerCatalog();

            EditorGUILayout.Space();
            _mode = GUILayout.Toolbar(_mode, Modes);
            EditorGUILayout.Space();

            // ---- commerce block (every DB field) ----
            EditorGUILayout.LabelField("SKU", EditorStyles.boldLabel);
            _id = EditorGUILayout.TextField("Id (kebab-case)", _id);
            _name = EditorGUILayout.TextField("Display name", _name);
            EditorGUILayout.LabelField("Description");
            _description = EditorGUILayout.TextArea(_description ?? "", GUILayout.MinHeight(36));
            if (_mode != 2)   // character SKUs take gender from the exported avatar itself
                _skuGender = (SkuGender)EditorGUILayout.EnumPopup("Gender (Unisex = anyone)", _skuGender);
            EditorGUILayout.BeginHorizontal();
            _price = EditorGUILayout.DoubleField("Price", _price);
            _currency = (PriceCurrency)EditorGUILayout.EnumPopup(_currency, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            _isStarter = EditorGUILayout.ToggleLeft("Starter (free, everyone owns)", _isStarter, GUILayout.Width(220));
            _exclusive = EditorGUILayout.ToggleLeft("Exclusive", _exclusive, GUILayout.Width(100));
            _enabled = EditorGUILayout.ToggleLeft("Enabled", _enabled, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();
            _sortOrder = EditorGUILayout.IntField("Sort order", _sortOrder);
            EditorGUILayout.Space();

            switch (_mode)
            {
                case 0: DrawItem(); break;
                case 1: DrawSet(); break;
                case 2: DrawCharacter(); break;
            }

            DrawServer();

            EditorGUILayout.Space();
            if (!string.IsNullOrEmpty(_log)) EditorGUILayout.HelpBox(_log, LogType(_log));
            else EditorGUILayout.HelpBox("Ready. Pick a piece and hit the green Save button.", MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        // ================================ status ================================

        /// <summary>Live state at a glance — the things that block a save, each with a red/green dot.</summary>
        private void DrawStatusStrip()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            StatusRow("Play mode", Application.isPlaying ? "ON" : "OFF — enter Play in the creator scene to read pieces + bake icons", Application.isPlaying);

            bool rigOk = _rig != null && _equipped.Count > 0;
            string rig = _rig == null ? "none — click “Find rig in scene”"
                       : _equipped.Count == 0 ? $"{_rig.name} — 0 pieces (dress it, then Refresh)"
                       : $"{_rig.name} — {_equipped.Count} equipped piece(s)";
            StatusRow("Rig", rig, rigOk);

            StatusRow("Server", $"{_apiUrl}   ·   JWT {(string.IsNullOrWhiteSpace(_jwt) ? "missing — click Auto" : "ready")}", !string.IsNullOrWhiteSpace(_jwt));

            int icons = _serverSkus.Count(s => s.hasIcon);
            StatusRow("Catalog (DB)", _serverErr != null ? $"error: {_serverErr}" : $"{_serverSkus.Count} SKU(s) · {icons} with icon", _serverErr == null);
            EditorGUILayout.EndVertical();
        }

        private static void StatusRow(string label, string value, bool ok)
        {
            EditorGUILayout.BeginHorizontal();
            var prev = GUI.color;
            GUI.color = ok ? OkColor : WarnColor;
            GUILayout.Label("●", GUILayout.Width(14));
            GUI.color = prev;
            GUILayout.Label(label, EditorStyles.miniBoldLabel, GUILayout.Width(84));
            GUILayout.Label(value, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>The SKUs actually in the DB (live) — so "why does it show only 1?" is answerable at a glance.</summary>
        private void DrawServerCatalog()
        {
            EditorGUILayout.BeginHorizontal();
            _showContents = EditorGUILayout.Foldout(_showContents, $"On the server  ({_serverSkus.Count})", true);
            if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(64))) RefreshServerCatalog();
            EditorGUILayout.EndHorizontal();
            if (!_showContents) return;

            EditorGUI.indentLevel++;
            if (_serverErr != null) EditorGUILayout.HelpBox("Couldn't read the server: " + _serverErr, MessageType.Warning);
            else if (_serverSkus.Count == 0) EditorGUILayout.LabelField("— empty — Save a piece to add one", EditorStyles.miniLabel);
            foreach (var s in _serverSkus)
            {
                EditorGUILayout.BeginHorizontal();
                var prev = GUI.color;
                GUI.color = s.hasIcon ? OkColor : WarnColor;
                GUILayout.Label(s.hasIcon ? "●" : "○", GUILayout.Width(16));
                GUI.color = prev;
                GUILayout.Label(s.id, EditorStyles.miniBoldLabel, GUILayout.Width(190));
                GUILayout.Label(s.type, EditorStyles.miniLabel, GUILayout.Width(64));
                GUILayout.Label(s.isStarter || s.price <= 0 ? "Free" : $"{s.price:0} {s.priceCurrency}", EditorStyles.miniLabel, GUILayout.Width(96));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;
        }

        private static MessageType LogType(string s) =>
            s.StartsWith("✓") ? MessageType.Info :
            s.StartsWith("⚠") ? MessageType.Warning :
            s.StartsWith("✗") ? MessageType.Error : MessageType.None;

        // ---- Item ----

        private void DrawItem()
        {
            EditorGUILayout.LabelField("Item", EditorStyles.boldLabel);
            if (_equipped.Count == 0) { EditorGUILayout.HelpBox("Play the creator scene, dress the rig, then Refresh equipped pieces.", MessageType.Warning); return; }

            int newIndex = EditorGUILayout.Popup("Piece", Mathf.Clamp(_pieceIndex, 0, _equipped.Count - 1), _equipped.Select(PieceLabel).ToArray());
            if (newIndex != _pieceIndex) { _pieceIndex = newIndex; AutoFillFromPiece(_equipped[_pieceIndex]); }
            var piece = _equipped[_pieceIndex];

            // Never let the id be empty on a selected piece — auto-derive from the path (distinct pieces ⇒ distinct ids).
            if (string.IsNullOrEmpty(_id)) ApplyPieceIdName(piece);
            if (GUILayout.Button("Suggest id + name from piece")) ApplyPieceIdName(piece);

            var pieceColors = CurrentColors(piece);
            string pieceKey = "i|" + piece.GetOutfitData().outfit + "|" + string.Join(",", pieceColors.Select(AvatarMapper.HexOf));
            DrawLivePreview(pieceKey, () => BakeBoothTexture(new List<(string, List<Color>)> { (piece.GetOutfitData().outfit, pieceColors) }));

            EditorGUILayout.HelpBox("The piece's CURRENT colours become its designed default (shop preview + initial equip).", MessageType.None);
            _colorMode = (ColorMode)EditorGUILayout.EnumPopup("Colour mode", _colorMode);

            if (_colorMode == ColorMode.Palette)
            {
                EditorGUILayout.LabelField($"Buyer's colour grid ({_palette.Count} swatches) — applies to ANY channel of this cloth:", EditorStyles.miniBoldLabel);
                int remove = -1;
                for (int i = 0; i < _palette.Count; i += 8)
                {
                    EditorGUILayout.BeginHorizontal();
                    for (int j = i; j < Mathf.Min(i + 8, _palette.Count); j++)
                    {
                        _palette[j] = EditorGUILayout.ColorField(GUIContent.none, _palette[j], false, false, false, GUILayout.Width(34), GUILayout.Height(22));
                        if (GUILayout.Button("×", GUILayout.Width(18), GUILayout.Height(22))) remove = j;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                if (remove >= 0) _palette.RemoveAt(remove);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("+ Swatch")) _palette.Add(Color.white);
                if (GUILayout.Button("+ Piece's current colours")) foreach (var c in CurrentColors(piece)) if (!_palette.Contains(c)) _palette.Add(c);
                if (GUILayout.Button("+ Standard 12")) foreach (var c in Standard12()) if (!_palette.Contains(c)) _palette.Add(c);
                if (GUILayout.Button("Clear")) _palette.Clear();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();
            DrawSaveState();
            if (PrimaryButton($"Save “{DisplayLabel()}” to shop   (→ DB + icon)"))
            {
                if (!Validate()) return;
                if (_colorMode == ColorMode.Palette && _palette.Count == 0) { _log = "✗ palette mode needs at least one swatch."; return; }
                var d = piece.GetOutfitData();
                var sku = NewSku("item");
                sku.slot = piece.Type != null ? piece.Type.name : "";
                sku.path = d.outfit;
                sku.colorMode = _colorMode == ColorMode.Palette ? "palette" : "fixed";
                sku.defaultColors = CurrentColors(piece).Select(AvatarMapper.HexOf).ToList();
                sku.palette = _colorMode == ColorMode.Palette ? _palette.Select(AvatarMapper.HexOf).ToList() : new List<string>();
                var png = BakePiecesPng(new List<(string, List<Color>)> { (d.outfit, CurrentColors(piece)) });
                SaveSku(sku, png);
            }
        }

        // ---- Set ----

        private void DrawSet()
        {
            EditorGUILayout.LabelField("Set (exclusive costume — full or partial)", EditorStyles.boldLabel);
            if (_equipped.Count == 0) { EditorGUILayout.HelpBox("Play the creator scene, dress the rig, then Refresh equipped pieces.", MessageType.Warning); return; }

            for (int i = 0; i < _equipped.Count; i++)
                _ticks[i] = EditorGUILayout.ToggleLeft(PieceLabel(_equipped[i]), _ticks[i]);

            var selected = _equipped.Where((o, i) => _ticks[i]).ToList();
            if (selected.Count > 0)
            {
                var data = selected.Select(o => (o.GetOutfitData().outfit, CurrentColors(o))).ToList();
                string setKey = "s|" + string.Join(";", data.Select(p => p.Item1 + ":" + string.Join(",", p.Item2.Select(AvatarMapper.HexOf))));
                DrawLivePreview(setKey, () => BakeBoothTexture(data));
            }

            EditorGUILayout.Space();
            DrawSaveState();
            if (PrimaryButton($"Save “{DisplayLabel()}” to shop   (→ DB + icon)"))
            {
                if (!Validate()) return;
                var pieces = new List<PieceDef>();
                for (int i = 0; i < _equipped.Count; i++)
                {
                    if (!_ticks[i]) continue;
                    var d = _equipped[i].GetOutfitData();
                    pieces.Add(new PieceDef { path = d.outfit, colors = CurrentColors(_equipped[i]).Select(AvatarMapper.HexOf).ToList() });
                }
                if (pieces.Count == 0) { _log = "✗ tick at least one piece."; return; }

                var sku = NewSku("set");
                sku.pieces = pieces;
                var png = BakePiecesPng(_equipped.Where((o, i) => _ticks[i]).Select(o => (o.GetOutfitData().outfit, CurrentColors(o))).ToList());
                SaveSku(sku, png);
            }
        }

        // ---- Character ----

        private void DrawCharacter()
        {
            EditorGUILayout.LabelField("Character (sellable premade avatar)", EditorStyles.boldLabel);
            _config = (AvatarConfig)EditorGUILayout.ObjectField("Avatar Config (roster)", _config, typeof(AvatarConfig), false);
            if (_config == null || _config.roster == null || _config.roster.Count == 0)
            { EditorGUILayout.HelpBox("Assign the AvatarConfig (for the base roster).", MessageType.Warning); return; }

            _gender = (AvatarConfig.Gender)EditorGUILayout.EnumPopup("Gender", _gender);
            var bases = _config.roster.Where(b => b != null && b.gender == _gender).ToList();
            if (bases.Count == 0) { EditorGUILayout.HelpBox("Roster has no bases for that gender.", MessageType.Warning); return; }
            _baseIndex = Mathf.Clamp(_baseIndex, 0, bases.Count - 1);
            _baseIndex = EditorGUILayout.Popup("Base loaded on the rig", _baseIndex, bases.Select(b => b.id).ToArray());
            EditorGUILayout.HelpBox("Pick the SAME base you loaded in the creator — the export stores the rig as a diff over it.", MessageType.None);

            DrawLivePreview("c|" + (_rig != null ? _rig.GetInstanceID() : 0), BakeRigTexture);
            if (GUILayout.Button("Refresh character preview")) _liveKey = null;

            EditorGUILayout.Space();
            DrawSaveState();
            if (PrimaryButton($"Save “{DisplayLabel()}” to shop   (→ DB + icon)"))
            {
                if (!Validate()) return;
                if (_rig == null) { _log = "✗ assign the rig."; return; }

                var data = BMAC_SaveSystem.GetCharacterData(_rig);
                var avatar = AvatarMapper.FromCharacter(data, _gender.ToString(), bases[_baseIndex].id);
                var sku = NewSku("character");
                sku.character = CharacterDef.From(avatar);
                SaveSku(sku, BakeRigPng());
            }
        }

        /// <summary>Label for the primary button + save-state line: the name if set, else the id.</summary>
        private string DisplayLabel() => !string.IsNullOrWhiteSpace(_name) ? _name.Trim() : (string.IsNullOrEmpty(_id) ? "this item" : _id);

        /// <summary>Tells the user, before they click, whether the current id is already on the server.</summary>
        private void DrawSaveState()
        {
            if (string.IsNullOrEmpty(_id)) { EditorGUILayout.HelpBox("Pick a piece — the id fills in automatically.", MessageType.None); return; }
            bool onServer = _serverSkus.Any(s => s.id == _id);
            EditorGUILayout.HelpBox(onServer
                ? $"“{_id}” is already on the server — Save updates it."
                : $"“{_id}” is NOT on the server yet — click Save to add it.", onServer ? MessageType.Info : MessageType.Warning);
        }

        // ---- Server section ----

        private void DrawServer()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            _apiUrl = EditorGUILayout.TextField("API base URL", _apiUrl);
            EditorPrefs.SetString(ApiPref, _apiUrl);
            EditorGUILayout.BeginHorizontal();
            _jwt = EditorGUILayout.TextField("JWT (auto from local login)", _jwt);
            if (GUILayout.Button("Auto", GUILayout.Width(46)))
            {
                _jwt = LoginWithSavedCreds() ?? PeekLocalToken();
                _log = string.IsNullOrEmpty(_jwt) ? "✗ no local login found — Play from Boot once (or paste a JWT)." : "✓ signed in with the device account.";
                RefreshServerCatalog();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ================================ server I/O ================================

        /// <summary>Save ONE SKU straight to the DB: upsert the row, then upload its baked icon. No local file.</summary>
        private void SaveSku(SkuDef sku, byte[] iconPng)
        {
            if (string.IsNullOrWhiteSpace(_jwt)) { _log = "✗ no JWT — click Auto in the Server section."; return; }

            string body = JsonUtility.ToJson(new CatalogFile { skus = new List<SkuDef> { sku } });
            var (code, resp) = Post("/api/shop/cosmetics/import", Encoding.UTF8.GetBytes(body), "application/json");
            if (code == 401)   // stale token → re-login with saved device creds and retry once
            {
                var fresh = LoginWithSavedCreds();
                if (fresh != null) { _jwt = fresh; (code, resp) = Post("/api/shop/cosmetics/import", Encoding.UTF8.GetBytes(body), "application/json"); }
            }
            if (code < 200 || code >= 300)
            {
                _log = $"✗ save failed ({code}): {resp}";
                Debug.LogError($"[CosmeticExporter] {_log}");
                return;
            }

            string iconMsg;
            if (iconPng == null) iconMsg = " · ⚠ no icon baked";
            else
            {
                var (ic, ib) = Post($"/api/shop/cosmetics/{sku.id}/icon", iconPng, "image/png");
                iconMsg = (ic >= 200 && ic < 300) ? " · icon uploaded" : $" · icon FAILED ({ic}: {ib})";
            }

            _log = $"✓ saved '{sku.id}' to the server{iconMsg}.";
            Debug.Log($"[CosmeticExporter] {_log}");
            RefreshServerCatalog();
        }

        /// <summary>Pull the live catalog from the DB (GET /api/shop/cosmetics) into the "On the server" list.</summary>
        private void RefreshServerCatalog()
        {
            _catalogLoaded = true;
            if (string.IsNullOrWhiteSpace(_jwt)) { _serverSkus = new List<ServerSku>(); _serverErr = "no JWT — click Auto"; return; }

            var (code, body) = Get("/api/shop/cosmetics");
            if (code == 401)
            {
                var fresh = LoginWithSavedCreds();
                if (fresh != null) { _jwt = fresh; (code, body) = Get("/api/shop/cosmetics"); }
            }
            if (code < 200 || code >= 300) { _serverSkus = new List<ServerSku>(); _serverErr = $"({code}) {body}"; return; }

            var wrap = JsonUtility.FromJson<ServerCatalog>(body);
            _serverSkus = wrap?.skus ?? new List<ServerSku>();
            _serverErr = null;
        }

        private (long code, string body) Post(string route, byte[] payload, string contentType)
        {
            using var req = new UnityWebRequest(_apiUrl.TrimEnd('/') + route, "POST");
            req.uploadHandler = new UploadHandlerRaw(payload);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", contentType);
            if (!string.IsNullOrWhiteSpace(_jwt)) req.SetRequestHeader("Authorization", "Bearer " + _jwt.Trim());
            return Wait(req);
        }

        private (long code, string body) Get(string route)
        {
            using var req = UnityWebRequest.Get(_apiUrl.TrimEnd('/') + route);
            if (!string.IsNullOrWhiteSpace(_jwt)) req.SetRequestHeader("Authorization", "Bearer " + _jwt.Trim());
            return Wait(req);
        }

        private static (long code, string body) Wait(UnityWebRequest req)
        {
            var op = req.SendWebRequest();
            double start = EditorApplication.timeSinceStartup;
            while (!op.isDone && EditorApplication.timeSinceStartup - start < 30) System.Threading.Thread.Sleep(25);
            return (req.responseCode, req.downloadHandler != null ? (req.downloadHandler.text ?? req.error) : req.error);
        }

        // ---- JWT from the local device login ----

        // The client save (persistentDataPath/client_save.json) nests each section as ESCAPED JSON inside Records[].Data,
        // so fields appear as \"Token\":\"...\" — match with optional backslashes before the quotes.
        private static string SaveField(string text, string field)
        {
            var m = Regex.Match(text, "\\\\?\"" + field + "\\\\?\"\\s*:\\s*\\\\?\"([^\"\\\\]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string ReadSaveFile()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "client_save.json");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch { return null; }
        }

        /// <summary>Cheap, no network: the live AccountManager token (Play via Boot), else the token stored in the save.</summary>
        private static string PeekLocalToken()
        {
            if (Application.isPlaying && PlayCard.Account.AccountManager.Instance != null)
            {
                string live = PlayCard.Account.AccountManager.Instance.JwtToken;
                if (!string.IsNullOrEmpty(live)) return live;
            }
            var text = ReadSaveFile();
            return text != null ? SaveField(text, "Token") ?? "" : "";
        }

        /// <summary>Fresh JWT: log in with the device-guest credentials stored in the save (survives token expiry).</summary>
        private string LoginWithSavedCreds()
        {
            var text = ReadSaveFile();
            if (text == null) return null;
            string email = SaveField(text, "Email"), password = SaveField(text, "Password");
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) return null;

            var payload = Encoding.UTF8.GetBytes($"{{\"email\":\"{email}\",\"password\":\"{password}\"}}");
            var (code, body) = Post("/api/auth/login", payload, "application/json");
            if (code < 200 || code >= 300) { Debug.LogWarning($"[CosmeticExporter] auto-login failed ({code}): {body}"); return null; }
            var m = Regex.Match(body ?? "", "\"token\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        // ================================ live preview ================================

        /// <summary>Live product-shot preview: re-bakes when <paramref name="key"/> (selection + colours) changes, so the
        /// icon is visible the moment a piece is picked — exactly what the saved shop icon will be.</summary>
        private void DrawLivePreview(string key, Func<Texture2D> bake)
        {
            if (Application.isPlaying && key != _liveKey)
            {
                var tex = bake();
                if (tex != null)
                {
                    if (_liveTex != null) DestroyImmediate(_liveTex);
                    _liveTex = tex;
                    _liveKey = key;
                }
            }
            if (_liveTex == null) return;

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var rect = GUILayoutUtility.GetRect(160, 160, GUILayout.Width(160), GUILayout.Height(160));
            EditorGUI.DrawTextureTransparent(rect, _liveTex, ScaleMode.ScaleToFit);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("live preview — exactly what the shop icon will be", EditorStyles.centeredGreyMiniLabel);
        }

        // ================================ icon booth ================================

        private byte[] BakePiecesPng(List<(string path, List<Color> colors)> pieces)
        {
            var tex = BakeBoothTexture(pieces);
            if (tex == null) return null;
            var png = tex.EncodeToPNG();
            DestroyImmediate(tex);
            return png;
        }

        private byte[] BakeRigPng()
        {
            var tex = BakeRigTexture();
            if (tex == null) return null;
            var png = tex.EncodeToPNG();
            DestroyImmediate(tex);
            return png;
        }

        /// <summary>Garment-only shot: instantiate the outfit prefab(s) STANDALONE (no OutfitSystem parent → BoZo's
        /// self-attach no-ops, so they render in bind pose instead of being hidden for a merge). Colours go through the
        /// per-instance MaterialPropertyBlock — the shared material asset is never touched. Returns null on failure.</summary>
        private Texture2D BakeBoothTexture(List<(string path, List<Color> colors)> pieces)
        {
            if (!Application.isPlaying) return null;

            var root = new GameObject("IconBooth");
            try
            {
                root.transform.position = new Vector3(0f, -1000f, 0f);
                bool any = false;
                foreach (var (path, colors) in pieces)
                {
                    var prefab = Resources.Load<Outfit>(path);
                    if (prefab == null) { Debug.LogWarning($"[CosmeticExporter] outfit not found for icon: {path}"); continue; }
                    var inst = Instantiate(prefab, root.transform);
                    inst.transform.localPosition = Vector3.zero;
                    inst.transform.localRotation = Quaternion.identity;
                    for (int c = 0; c < colors.Count; c++) inst.SetColor(colors[c], c + 1);
                    inst.UpdateMaterialBlock();
                    any = true;
                }
                return any ? SnapTexture(root, ViewDirFor(pieces, root.transform)) : null;
            }
            finally { DestroyImmediate(root); }
        }

        /// <summary>Per-slot camera angle. Flat garments (tops/bottoms) read best straight-on; footwear reads best from
        /// a CLOSE high front-side angle (you're looking at the top of the shoe). Applied only when the whole bake is
        /// footwear (a mixed set keeps the front shot).</summary>
        private static Vector3 ViewDirFor(List<(string path, List<Color> colors)> pieces, Transform subject)
        {
            bool allFootwear = pieces.Count > 0 && pieces.All(p =>
            {
                string slot = (p.path ?? "").Split('/')[0].ToLowerInvariant();
                return slot == "feet" || slot == "socks";
            });
            if (!allFootwear) return subject.forward;
            // High front-right: down onto the toe box, slightly from the side — the classic sneaker-shop angle.
            return (subject.forward * 0.6f + Vector3.up * 1.0f + subject.right * 0.55f).normalized;
        }

        /// <summary>Full-character shot of the CURRENT dressed rig (character SKUs): move it to the booth layer, snap, restore.</summary>
        private Texture2D BakeRigTexture()
        {
            if (!Application.isPlaying || _rig == null) return null;
            var rigRoot = _rig.transform.root.gameObject;
            var saved = new Dictionary<GameObject, int>();
            foreach (var t in rigRoot.GetComponentsInChildren<Transform>(true)) { saved[t.gameObject] = t.gameObject.layer; t.gameObject.layer = BoothLayer; }
            try { return SnapTexture(rigRoot); }
            finally { foreach (var kv in saved) if (kv.Key != null) kv.Key.layer = kv.Value; }
        }

        /// <summary>
        /// Photograph the garment so EVERY icon is framed identically — the piece fills the frame to a fixed
        /// <see cref="IconPadPx"/> margin, regardless of shape. Two passes, because a SkinnedMeshRenderer's bounds are
        /// a padded worst-case box: pass 1 is a wide shot; we measure the REAL content box from the rendered alpha and
        /// pass 2 reframes onto it. Aspect is preserved (no stretch); the dominant axis hits the margin, the other centres.
        /// </summary>
        private Texture2D SnapTexture(GameObject subject, Vector3? viewDir = null)
        {
            foreach (var t in subject.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = BoothLayer;

            var renderers = subject.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled).ToList();
            if (renderers.Count == 0) { Debug.LogWarning("[CosmeticExporter] nothing to photograph — icon skipped."); return null; }
            var b = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) b.Encapsulate(r.bounds);
            var forward = viewDir ?? subject.transform.forward;

            float size0 = Mathf.Max(b.extents.x, b.extents.y, b.extents.z) * 1.6f + 0.05f;
            var wide = RenderShot(b.center, forward, size0, out Vector3 right, out Vector3 up);
            var px = wide.GetPixels32();
            DestroyImmediate(wide);

            int minX = IconSize, minY = IconSize, maxX = -1, maxY = -1;
            for (int y = 0; y < IconSize; y++)
                for (int x = 0; x < IconSize; x++)
                    if (px[y * IconSize + x].a > 10)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            if (maxX < 0) { Debug.LogWarning("[CosmeticExporter] rendered empty — icon skipped."); return null; }

            float worldPerPx = 2f * size0 / IconSize;
            Vector3 centre = b.center
                + right * (((minX + maxX + 1) * 0.5f - IconSize * 0.5f) * worldPerPx)
                + up * (((minY + maxY + 1) * 0.5f - IconSize * 0.5f) * worldPerPx);
            float halfW = (maxX - minX + 1) * 0.5f * worldPerPx;
            float halfH = (maxY - minY + 1) * 0.5f * worldPerPx;
            float size1 = Mathf.Max(halfW, halfH) * (IconSize / (IconSize - 2f * IconPadPx));

            return RenderShot(centre, forward, size1, out _, out _);
        }

        private Texture2D RenderShot(Vector3 centre, Vector3 forward, float orthoSize, out Vector3 right, out Vector3 up)
        {
            var camGo = new GameObject("IconBoothCam");
            var rt = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32);
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = orthoSize;
                cam.aspect = 1f;
                cam.cullingMask = 1 << BoothLayer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 1000f;
                cam.transform.position = centre + forward.normalized * 10f;
                cam.transform.LookAt(centre, Vector3.up);
                right = cam.transform.right;
                up = cam.transform.up;
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                return tex;
            }
            finally
            {
                DestroyImmediate(camGo);
                rt.Release();
                DestroyImmediate(rt);
            }
        }

        // ================================ shared plumbing ================================

        /// <summary>The one big green primary action.</summary>
        private static bool PrimaryButton(string label)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.30f, 0.64f, 0.38f);
            bool clicked = GUILayout.Button(label, GUILayout.Height(38));
            GUI.backgroundColor = prev;
            return clicked;
        }

        /// <summary>Fill id + name from a piece, but only if the id is blank or still our last auto-fill (never clobber a
        /// hand-typed id). Distinct pieces ⇒ distinct ids ⇒ no accidental overwrite.</summary>
        private void AutoFillFromPiece(Outfit piece)
        {
            if (piece == null) return;
            string suggested = Slug(piece.GetOutfitData().outfit);
            if (string.IsNullOrEmpty(_id) || _id == _autoId) ApplyPieceIdName(piece);
            _autoId = suggested;
        }

        private void ApplyPieceIdName(Outfit piece)
        {
            var d = piece.GetOutfitData();
            _id = Slug(d.outfit);
            _autoId = _id;
            _name = string.IsNullOrEmpty(piece.OutfitName) ? piece.name : piece.OutfitName;
        }

        private void RefreshEquipped()
        {
            _equipped = _rig != null ? (_rig.GetOutfits() ?? new List<Outfit>()).Where(o => o != null).ToList() : new List<Outfit>();
            while (_ticks.Count < _equipped.Count) _ticks.Add(true);
            while (_ticks.Count > _equipped.Count) _ticks.RemoveAt(_ticks.Count - 1);
            _pieceIndex = Mathf.Clamp(_pieceIndex, 0, Mathf.Max(0, _equipped.Count - 1));
            _log = _rig == null
                ? "✗ no rig — click Find rig in scene (in Play mode) or drag the dressed character in."
                : _equipped.Count == 0
                    ? $"✗ 0 pieces on '{_rig.name}' — wrong rig? Drag the character you're dressing into the Rig field."
                    : $"✓ {_equipped.Count} equipped pieces on '{_rig.name}'.";
        }

        private string PieceLabel(Outfit o)
        {
            var d = o.GetOutfitData();
            return $"{d.outfit}  ({(o.ColorChannels != null ? o.ColorChannels.Length : 0)} colour ch)";
        }

        /// <summary>A piece's current colours, trimmed to its REAL channel count.</summary>
        private static List<Color> CurrentColors(Outfit o)
        {
            var d = o.GetOutfitData();
            var colors = d.colors ?? new List<Color>();
            int channels = o.ColorChannels != null ? o.ColorChannels.Length : colors.Count;
            return colors.Take(Mathf.Min(channels, colors.Count)).ToList();
        }

        private static IEnumerable<Color> Standard12() => new[]
        {
            new Color(0.10f, 0.10f, 0.10f), new Color(0.95f, 0.95f, 0.95f), new Color(0.55f, 0.27f, 0.07f),
            new Color(0.75f, 0.10f, 0.10f), new Color(0.95f, 0.55f, 0.10f), new Color(0.95f, 0.85f, 0.20f),
            new Color(0.15f, 0.60f, 0.20f), new Color(0.10f, 0.45f, 0.85f), new Color(0.05f, 0.20f, 0.55f),
            new Color(0.55f, 0.15f, 0.65f), new Color(0.95f, 0.45f, 0.65f), new Color(0.50f, 0.50f, 0.50f),
        };

        private bool Validate()
        {
            if (!IdRx.IsMatch(_id ?? "")) { _log = "✗ SKU id must be kebab-case (e.g. top-leather-jacket)."; return false; }
            if (string.IsNullOrWhiteSpace(_name)) { _log = "✗ give the SKU a display name."; return false; }
            if (_price < 0) { _log = "✗ price can't be negative."; return false; }
            return true;
        }

        private SkuDef NewSku(string type) => new SkuDef
        {
            id = _id, type = type, name = _name.Trim(), description = (_description ?? "").Trim(),
            gender = type == "character" ? _gender.ToString()
                   : _skuGender == SkuGender.Unisex ? "" : _skuGender.ToString(),
            price = _price, priceCurrency = _currency.ToString(),
            isStarter = _isStarter, exclusive = _exclusive, enabled = _enabled, sortOrder = _sortOrder,
        };

        /// <summary>"Top/BSMC_Top_LeatherJacket" → "top-leather-jacket".</summary>
        private static string Slug(string path)
        {
            string s = (path ?? "").Replace("BSMC_", "");
            s = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1-$2");
            s = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-");
            var parts = s.Trim('-').Split('-').ToList();          // drop the duplicated slot prefix ("top-top-…")
            if (parts.Count > 1 && parts[0] == parts[1]) parts.RemoveAt(0);
            return string.Join("-", parts);
        }

        // ---- POST body DTOs (server import contract — camelCase) ----

        [Serializable] private class CatalogFile { public List<SkuDef> skus = new List<SkuDef>(); }

        [Serializable]
        private class SkuDef
        {
            public string id;
            public string type;                                       // item | set | character
            public string name;
            public string description;
            public string gender = "";                                // "Male" | "Female" | "" = unisex
            public string slot;                                       // item
            public string path;                                       // item
            public string colorMode = "fixed";                        // item: fixed | palette
            public List<string> defaultColors = new List<string>();   // item
            public List<string> palette = new List<string>();         // item palette mode
            public List<PieceDef> pieces = new List<PieceDef>();      // set
            public CharacterDef character;                            // character
            public double price;
            public string priceCurrency = "Kash";
            public bool isStarter;
            public bool exclusive;
            public bool enabled = true;
            public int sortOrder;
        }

        [Serializable] private class PieceDef { public string path; public List<string> colors = new List<string>(); }

        /// <summary>Serializable mirror of the server AvatarDto (AvatarData uses properties, which JsonUtility skips).</summary>
        [Serializable]
        private class CharacterDef
        {
            public string gender, baseId;
            public List<ShapeDef> body = new List<ShapeDef>(), face = new List<ShapeDef>();
            public List<ModDef> mods = new List<ModDef>();
            public List<PieceDef> outfits = new List<PieceDef>();

            public static CharacterDef From(AvatarData a) => new CharacterDef
            {
                gender = a.Gender, baseId = a.BaseId,
                body = a.Body.Select(s => new ShapeDef { key = s.Key, value = s.Value }).ToList(),
                face = a.Face.Select(s => new ShapeDef { key = s.Key, value = s.Value }).ToList(),
                mods = a.Mods.Select(m => new ModDef { bone = m.Bone, scale = m.Scale, sx = m.Sx, sy = m.Sy, sz = m.Sz, px = m.Px, py = m.Py, pz = m.Pz }).ToList(),
                outfits = a.Outfits.Select(o => new PieceDef { path = o.Path, colors = new List<string>(o.Colors ?? new List<string>()) }).ToList(),
            };
        }

        [Serializable] private class ShapeDef { public string key; public float value; }
        [Serializable] private class ModDef { public string bone; public float scale = 1, sx = 1, sy = 1, sz = 1, px, py, pz; }

        // ---- GET response DTOs (the live DB catalog) ----

        [Serializable] private class ServerCatalog { public List<ServerSku> skus = new List<ServerSku>(); }

        [Serializable]
        private class ServerSku
        {
            public string id;
            public string type;
            public string name;
            public string priceCurrency;
            public double price;
            public bool isStarter;
            public bool hasIcon;
        }
    }
}
