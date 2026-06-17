using UnityEngine;

namespace PlayCard.App
{
    /// <summary>
    /// Applies mobile-first runtime defaults at launch, before any scene loads — no GameObject
    /// needed. Caps the frame rate, turns off vSync so that cap is honoured, and keeps the screen
    /// awake during play (a card game can sit idle mid-decision).
    /// </summary>
    public static class MobileBootstrap
    {
        // 60 fps target. Revisit for 120 Hz screens once you've profiled the table scene.
        private const int TargetFrameRate = 60;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            // vSync off → Application.targetFrameRate actually takes effect.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = TargetFrameRate;

            // Don't let the device dim/sleep while the game is open.
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }
    }
}
