#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// One-click: bake lighting for every scene under <c>Assets/1Khela/_Scenes/Virtual_Worlds</c> so each
    /// scene owns its baked GI + reflection probes in its OWN folder (self-contained), instead of
    /// referencing the copied-from Synty demo's baked data by GUID.
    ///
    /// Run once after copying the Synty scenes in. Meshes/materials/props intentionally stay shared from the
    /// Synty package (referenced by GUID — never copied per scene). Baking is synchronous and can take a
    /// while on a busy nightclub scene; watch the Console. Editor-only.
    /// </summary>
    public static class VirtualWorldLightingBaker
    {
        private const string Root = "Assets/1Khela/_Scenes/Virtual_Worlds";

        [MenuItem("Khela/Bake Virtual-World Lighting")]
        public static void BakeAll()
        {
            // Don't clobber unsaved work; remember what was open so we can restore it after.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string restore = SceneManager.GetActiveScene().path;

            var guids = AssetDatabase.FindAssets("t:Scene", new[] { Root });
            if (guids.Length == 0) { Debug.LogWarning($"[VWBake] No scenes found under {Root}."); return; }

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    EditorUtility.DisplayProgressBar("Baking Virtual-World lighting",
                        $"{path}  ({i + 1}/{guids.Length})", (float)i / guids.Length);

                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    EnsureLocalLightingSettings(path);      // give the scene its OWN .lighting so the folder is self-contained
                    Debug.Log($"[VWBake] Baking {path} …");
                    Lightmapping.Bake();                    // synchronous; writes the sibling <scene>/ folder (LightingData + reflection probes) + re-points the scene
                    EditorSceneManager.SaveScene(scene);
                    Debug.Log($"[VWBake] Baked + saved {path}");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!string.IsNullOrEmpty(restore))
                EditorSceneManager.OpenScene(restore, OpenSceneMode.Single);

            AssetDatabase.Refresh();
            Debug.Log($"[VWBake] Done — {guids.Length} scene(s) now own their baked lighting under {Root}.");
        }

        /// <summary>
        /// Bake ONLY the currently-open scene (e.g. the Blackjack table, which lives OUTSIDE
        /// <see cref="Root"/> so <see cref="BakeAll"/> won't touch it). Same <c>Lightmapping.Bake()</c>
        /// as Unity's Lighting ▸ Generate Lighting, plus it gives the scene its own self-contained
        /// <c>.lighting</c> in its folder. Run <c>World Prep ▸ 2 - Prep Open Scene</c> first.
        /// </summary>
        [MenuItem("Khela/Bake Open Scene Lighting")]
        public static void BakeOpenScene()
        {
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                Debug.LogWarning("[VWBake] Save the open scene first — an unsaved scene has no folder to write baked data into.");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EnsureLocalLightingSettings(scene.path);   // scene owns its .lighting in its own folder
            Debug.Log($"[VWBake] Baking OPEN scene {scene.path} …");
            Lightmapping.Bake();                        // identical to Lighting ▸ Generate Lighting
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.Refresh();
            Debug.Log($"[VWBake] Baked + saved {scene.path}");
        }

        // Give the scene its OWN LightingSettings (.lighting) in its folder — copied from whatever it
        // currently uses (the Synty demo's), or a fresh default — so the folder is fully self-contained.
        // The baked-data folder (LightingData + reflection probes) is produced by Bake() itself.
        private static void EnsureLocalLightingSettings(string scenePath)
        {
            string folder = Path.GetDirectoryName(scenePath).Replace('\\', '/');
            string sceneName = Path.GetFileNameWithoutExtension(scenePath);
            string localPath = $"{folder}/{sceneName}Settings.lighting";

            var existing = AssetDatabase.LoadAssetAtPath<LightingSettings>(localPath);
            if (existing != null) { Lightmapping.lightingSettings = existing; return; }

            LightingSettings local = null;
            if (Lightmapping.TryGetLightingSettings(out var src) && src != null)
            {
                var srcPath = AssetDatabase.GetAssetPath(src);
                if (!string.IsNullOrEmpty(srcPath) && AssetDatabase.CopyAsset(srcPath, localPath))
                    local = AssetDatabase.LoadAssetAtPath<LightingSettings>(localPath);
            }
            if (local == null)
            {
                local = new LightingSettings { name = sceneName + "Settings" };
                AssetDatabase.CreateAsset(local, localPath);
            }
            Lightmapping.lightingSettings = local;
        }
    }
}
#endif
