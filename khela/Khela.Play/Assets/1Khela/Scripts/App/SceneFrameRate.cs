using UnityEngine;
using PlayCard.Quality;

namespace PlayCard.App
{
    /// <summary>
    /// Per-scene frame-rate PREFERENCE, clamped to the graphics tier's ceiling. Drop this on a root object in
    /// any scene to say what that scene *wants* — e.g. 30 fps on menus (Home/Lobby) to cut GPU/heat, 60 on the
    /// table where smoothness matters.
    ///
    /// The effective cap is <c>min(this scene's pref, GraphicsQualityManager.FpsCeiling)</c>, so a 60-fps table
    /// still caps to 30 on a Low/Mid-tier device — the tier owns the device ceiling, the scene can only ask for
    /// *less*. That resolves the two systems fighting over the global <c>Application.targetFrameRate</c> (which
    /// persists across scene loads). vSync is forced off so the cap is honoured.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneFrameRate : MonoBehaviour
    {
        [Tooltip("Target FPS this scene WANTS. Clamped down to the graphics tier's ceiling (Low/Mid = 30, " +
                 "High/Ultra = 60). Menus: 30. Gameplay: 60.")]
        [SerializeField, Range(15, 120)] private int targetFps = 30;

        private void OnEnable() => Apply();

        private void Apply()
        {
            QualitySettings.vSyncCount = 0;   // vSync off → targetFrameRate is respected
            int ceiling = GraphicsQualityManager.FpsCeiling(GraphicsQualityManager.Current);
            int fps = Mathf.Min(targetFps, ceiling);
            Application.targetFrameRate = fps;
            Debug.Log($"[SceneFrameRate] {fps} fps for '{gameObject.scene.name}' " +
                      $"(scene wants {targetFps}, tier ceiling {ceiling}).");
        }
    }
}
