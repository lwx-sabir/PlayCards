#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PlayCard.EditorTools
{
    /// <summary>Dev helper: wipe the local guest save (auth credentials/token) so the next Play
    /// re-registers a fresh device account. Tools ▸ Khela ▸ Clear Local Save.</summary>
    public static class ClearSaveMenu
    {
        [MenuItem("Tools/Khela/Clear Local Save")]
        public static void ClearSave()
        {
            var path = Path.Combine(Application.persistentDataPath, "client_save.json");
            if (File.Exists(path))
            {
                File.Delete(path);
                Debug.Log($"[Khela] Cleared local save: {path}");
            }
            else
            {
                Debug.Log($"[Khela] No save file at {path}");
            }
        }
    }
}
#endif
