using UnityEngine;
using CodeStage.AdvancedFPSCounter;

namespace PlayCard.App
{
    /// <summary>
    /// Single source of truth for whether the on-screen FPS/memory overlay (Codestage AFPS) is drawn. Backed by a
    /// persisted pref so the choice survives restarts, and applied to the live keepAlive AFPS instance spawned in
    /// <see cref="Bootstrapper"/>. Both the boot-time spawn and the in-game Settings toggle go through here so they
    /// can never disagree.
    ///
    /// The overlay still SPAWNS in every build (that's Bootstrapper.SpawnFpsOverlay's job) — this only controls its
    /// visibility. Default is visible: we're pre-launch and profiling. For a real store build, flip the default in
    /// <see cref="Visible"/>'s getter to 0 (or hide the Settings toggle from players).
    /// </summary>
    public static class FpsOverlay
    {
        private const string PrefKey = "khela.showFps";

        /// <summary>Whether the overlay is shown. Assigning persists the choice AND applies it to the live overlay.</summary>
        public static bool Visible
        {
            get => PlayerPrefs.GetInt(PrefKey, 1) == 1;   // default 1 = visible (profiling); set 0 for a store build
            set
            {
                PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
                PlayerPrefs.Save();
                Apply(value);
            }
        }

        /// <summary>Push the persisted state onto the live AFPS instance. No-op if AFPS hasn't spawned yet.</summary>
        public static void Apply() => Apply(Visible);

        private static void Apply(bool visible)
        {
            var afps = AFPSCounter.Instance;
            if (afps != null)
                afps.OperationMode = visible ? OperationMode.Normal : OperationMode.Disabled;
        }
    }
}
