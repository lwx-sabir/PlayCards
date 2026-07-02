#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Optionally force Play mode to always start from the Boot scene (so the bootstrap + navigation always
    /// run) no matter which scene is open. Toggle via <b>Tools ▸ Khela ▸ Play From Boot</b> (checkmark shows
    /// the state). When <b>OFF</b>, Play runs the currently-open scene — handy for opening demo/sandbox scenes
    /// directly. The choice persists per-user via <see cref="EditorPrefs"/> and re-applies on every editor
    /// load/recompile. Default = ON (preserves the original behaviour). Editor-only.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayFromBoot
    {
        private const string BootScenePath = "Assets/1Khela/_Scenes/Boot.unity";
        private const string MenuPath = "Tools/Khela/Play From Boot";
        private const string PrefKey = "Khela.PlayFromBoot.Enabled";

        /// <summary>Per-user toggle; default ON so existing behaviour is unchanged until someone flips it.</summary>
        private static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        static PlayFromBoot() => Apply();

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            Enabled = !Enabled;
            Apply();
        }

        // Draws the checkmark next to the menu item to reflect the current state.
        [MenuItem(MenuPath, isValidateFunction: true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;   // always clickable
        }

        private static void Apply()
        {
            if (Enabled)
            {
                var boot = AssetDatabase.LoadAssetAtPath<SceneAsset>(BootScenePath);
                if (boot != null) EditorSceneManager.playModeStartScene = boot;
            }
            else
            {
                // Null = Unity plays whichever scene is currently open (normal default).
                EditorSceneManager.playModeStartScene = null;
            }
        }
    }
}
#endif
