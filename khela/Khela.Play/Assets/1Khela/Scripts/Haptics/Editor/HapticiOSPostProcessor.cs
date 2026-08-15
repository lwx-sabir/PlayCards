#if UNITY_EDITOR && UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace PlayCard.Haptics
{
    /// <summary>
    /// Links <c>CoreHaptics.framework</c> into the generated Xcode project so <c>KhelaHaptics.mm</c> compiles. Runs
    /// only when the active build target is iOS (that's when <c>UNITY_IOS</c> is defined and the Xcode APIs exist).
    /// The framework is WEAK-linked: the .mm is entirely iOS-13-gated, so a pre-13 device must not hard-require
    /// CoreHaptics at launch (dyld would fail to load the app before any @available guard could run).
    /// </summary>
    public static class HapticiOSPostProcessor
    {
        [PostProcessBuild]
        public static void OnPostProcessBuild(BuildTarget buildTarget, string path)
        {
            if (buildTarget != BuildTarget.iOS) return;

            string projectPath = PBXProject.GetPBXProjectPath(path);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);

            string targetGuid = project.GetUnityFrameworkTargetGuid();
            project.AddFrameworkToProject(targetGuid, "CoreHaptics.framework", true); // true = weak link

            File.WriteAllText(projectPath, project.WriteToString());
        }
    }
}
#endif
