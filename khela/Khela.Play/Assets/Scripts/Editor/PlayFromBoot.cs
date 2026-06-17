#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Always enter Play mode from the Boot scene (build index 0), no matter which scene is open, so the
    /// bootstrap + navigation always run. Re-applies on every editor load/recompile. Editor-only.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayFromBoot
    {
        private const string BootScenePath = "Assets/1Khela/_Scenes/Boot.unity";

        static PlayFromBoot()
        {
            var boot = AssetDatabase.LoadAssetAtPath<SceneAsset>(BootScenePath);
            if (boot != null) EditorSceneManager.playModeStartScene = boot;
        }
    }
}
#endif
