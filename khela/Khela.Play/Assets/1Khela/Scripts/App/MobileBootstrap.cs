using UnityEngine;

namespace PlayCard.App
{
    /// <summary>
    /// Applies mobile-first runtime defaults at launch, before any scene loads — no GameObject needed.
    /// Turns off vSync (so the frame-rate cap is honoured) and keeps the screen awake during play (a card
    /// game can sit idle mid-decision). The frame-rate cap itself is now owned by
    /// <c>GraphicsQualityManager</c> (per-tier ceiling) + <c>SceneFrameRate</c> (per-scene preference), so
    /// it is deliberately NOT set here.
    /// </summary>
    public static class MobileBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            QualitySettings.vSyncCount = 0;                 // vSync off → the fps cap actually takes effect
            Screen.sleepTimeout = SleepTimeout.NeverSleep;  // don't dim/sleep mid-decision
        }
    }
}
