using UnityEngine;
using TMPro;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PlayCard.Chests
{
    /// <summary>Client chest rarity — mirrors the server tier names ("Common"/"Uncommon"/"Rare").</summary>
    public enum ChestTier { Common, Uncommon, Rare }

    /// <summary>
    /// Identity + optional display text for a chest object. Put this on a chest GameObject/prefab and set its <see cref="key"/>
    /// — type it, or use the inspector's <b>Pick chest from server…</b> button to choose a real one. Title/Description are
    /// OPTIONAL; if the TMP fields are assigned they're filled on enable. The reward UI reads <see cref="key"/> +
    /// <see cref="tier"/> to know which chest this object represents.
    /// </summary>
    public sealed class Chest : MonoBehaviour
    {
        [Tooltip("Server chest key, e.g. \"CK_Chest\". Type it, or use 'Pick chest from server…' below.")]
        public string key = "CK_Chest";
        [Tooltip("Common / Uncommon / Rare.")]
        public ChestTier tier = ChestTier.Common;

        [Header("Optional display text")]
        [TextArea] public string title;
        [TextArea] public string description;

        [Header("Optional auto-fill targets")]
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text descriptionText;

        private void OnEnable() => Apply();

        /// <summary>Push title/description into the assigned TMP fields, if any (skips blank overrides).</summary>
        public void Apply()
        {
            if (titleText != null && !string.IsNullOrEmpty(title)) titleText.text = title;
            if (descriptionText != null && !string.IsNullOrEmpty(description)) descriptionText.text = description;
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(Chest))]
    public sealed class ChestEditor : Editor
    {
        [System.Serializable] private sealed class ChestInfo { public string key; public string tier; public string title; public string description; }
        [System.Serializable] private sealed class ChestList { public ChestInfo[] chests; }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (GUILayout.Button("Pick chest from server…"))
                FetchAndShow((Chest)target);
        }

        private static void FetchAndShow(Chest chest)
        {
            string url = ResolveBaseUrl().TrimEnd('/') + "/api/chests";
            string json = null, error = null;
            try
            {
                using var req = UnityEngine.Networking.UnityWebRequest.Get(url);
                var op = req.SendWebRequest();
                while (!op.isDone) System.Threading.Thread.Sleep(15);   // editor button — brief block is fine
                if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success) json = req.downloadHandler.text;
                else error = $"{req.responseCode} {req.error}";
            }
            catch (System.Exception e) { error = e.Message; }

            if (string.IsNullOrEmpty(json))
            {
                EditorUtility.DisplayDialog("Pick chest", $"Couldn't reach the server.\n\n{url}\n\n{error}", "OK");
                return;
            }

            ChestList list;
            try { list = JsonUtility.FromJson<ChestList>(json); } catch { list = null; }
            if (list == null || list.chests == null || list.chests.Length == 0)
            {
                EditorUtility.DisplayDialog("Pick chest", "No chests returned from the server.", "OK");
                return;
            }

            var menu = new GenericMenu();
            foreach (var info in list.chests)
            {
                var c = info;   // capture per-iteration
                menu.AddItem(new GUIContent($"{c.key} / {c.tier}"), false, () =>
                {
                    Undo.RecordObject(chest, "Pick chest");
                    chest.key = c.key;
                    if (System.Enum.TryParse<ChestTier>(c.tier, true, out var t)) chest.tier = t;
                    if (string.IsNullOrEmpty(chest.title)) chest.title = c.title;
                    if (string.IsNullOrEmpty(chest.description)) chest.description = c.description;
                    EditorUtility.SetDirty(chest);
                });
            }
            menu.ShowAsContext();
        }

        // Backend base URL from any AppConfig asset in the project, else the localhost default.
        private static string ResolveBaseUrl()
        {
            var guids = AssetDatabase.FindAssets("t:AppConfig");
            if (guids != null && guids.Length > 0)
            {
                var cfg = AssetDatabase.LoadAssetAtPath<PlayCard.Core.AppConfig>(AssetDatabase.GUIDToAssetPath(guids[0]));
                if (cfg != null && !string.IsNullOrWhiteSpace(cfg.BaseApiUrl)) return cfg.BaseApiUrl;
            }
            return "http://localhost:5044";
        }
    }
#endif
}
