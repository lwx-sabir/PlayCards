using UnityEngine;

namespace PlayCard.App
{
    /// <summary>
    /// Applies mobile-first runtime defaults at launch, before any scene loads — no GameObject needed.
    /// Turns off vSync (so the frame-rate cap is honoured), sets a boot-default target frame rate, and keeps
    /// the screen awake during play (a card game can sit idle mid-decision). Each scene's <c>SceneFrameRate</c>
    /// then overrides the target per scene (menus 30 / gameplay 60), independent of the graphics tier.
    /// </summary>
    public static class MobileBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            QualitySettings.vSyncCount = 0;                 // vSync off → the fps cap actually takes effect
            Application.targetFrameRate = 60;               // boot default; SceneFrameRate overrides per scene
            Screen.sleepTimeout = SleepTimeout.NeverSleep;  // don't dim/sleep mid-decision
        }
    }
}
