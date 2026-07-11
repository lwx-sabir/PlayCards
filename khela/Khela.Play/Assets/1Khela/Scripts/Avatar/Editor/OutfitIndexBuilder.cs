using System.Collections.Generic;
using System.Linq;
using Bozo.ModularCharacters;
using UnityEditor;
using UnityEngine;

namespace PlayCard.Avatar.EditorTools
{
    /// <summary>
    /// Builds the mobile-safe <see cref="OutfitIndex"/> once, at edit time: scans every Outfit in Resources and records
    /// only its path / slot / label / icon (no meshes). Menu: Khela ▸ Avatar ▸ Build Outfit Index. Re-run whenever
    /// outfits are added/removed. Auto-assigns the index to any AvatarConfig that doesn't have one.
    /// </summary>
    public static class OutfitIndexBuilder
    {
        private const string AssetPath = "Assets/1Khela/OutfitIndex.asset";

        [MenuItem("Khela/Avatar/Build Outfit Index")]
        public static void Build()
        {
            var index = AssetDatabase.LoadAssetAtPath<OutfitIndex>(AssetPath);
            if (index == null)
            {
                index = ScriptableObject.CreateInstance<OutfitIndex>();
                AssetDatabase.CreateAsset(index, AssetPath);
            }

            index.entries.Clear();
            var seen = new HashSet<string>();
            foreach (var o in Resources.LoadAll<Outfit>(""))
            {
                if (o == null || !o.showCharacterCreator || o.Type == null || string.IsNullOrEmpty(o.Type.name)) continue;
                string path = o.Type.name + "/" + o.name;   // == the persisted OutfitData.outfit (Clone-stripped)
                if (!seen.Add(path)) continue;
                index.entries.Add(new OutfitIndex.Entry
                {
                    path = path,
                    slot = o.Type.name,
                    label = string.IsNullOrEmpty(o.OutfitName) ? o.name : o.OutfitName,
                    icon = o.OutfitIcon,
                });
            }

            EditorUtility.SetDirty(index);
            AssetDatabase.SaveAssets();

            // Point any AvatarConfig without an index at this one.
            foreach (var guid in AssetDatabase.FindAssets("t:AvatarConfig"))
            {
                var cfg = AssetDatabase.LoadAssetAtPath<AvatarConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (cfg != null && cfg.outfitIndex == null) { cfg.outfitIndex = index; EditorUtility.SetDirty(cfg); }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = index;
            EditorGUIUtility.PingObject(index);

            var bySlot = index.entries.GroupBy(e => e.slot).OrderBy(g => g.Key);
            Debug.Log($"[OutfitIndex] built {index.entries.Count} entries → {AssetPath}. Slots: " +
                      string.Join(", ", bySlot.Select(g => $"{g.Key}({g.Count()})")));

            // Unload the meshes the scan pulled in (edit-time only; keeps the editor lean after building).
            Resources.UnloadUnusedAssets();
        }
    }
}
