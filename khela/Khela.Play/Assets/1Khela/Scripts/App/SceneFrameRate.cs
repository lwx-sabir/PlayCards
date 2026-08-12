using UnityEngine;

namespace PlayCard.App
{
    /// <summary>
    /// Per-scene frame-rate target — this scene's value IS the target. Drop it on a root object in any scene:
    /// e.g. 30 fps on menus (Home/Lobby) to cut GPU/heat, 60 on the table where smoothness matters.
    ///
    /// FPS is INDEPENDENT of the graphics tier: the tier controls visual quality (render scale, shadows, SSAO)
    /// only — it does NOT cap fps. So a 60-fps gameplay scene targets 60 on every tier; because
    /// <c>Application.targetFrameRate</c> is a ceiling (not a floor), a weak device simply runs at whatever it
    /// achieves rather than being force-capped. A "Battery Saver" toggle can force a lower cap globally later.
    /// vSync is forced off so the cap is honoured.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneFrameRate : MonoBehaviour
    {
        [Tooltip("Target FPS for this scene (a ceiling, not a guarantee). Menus: 30. Gameplay: 60. " +
                 "Independent of the graphics tier.")]
        [SerializeField, Range(15, 120)] private int targetFps = 30;

        private void OnEnable() => Apply();

        private void Apply()
        {
            QualitySettings.vSyncCount = 0;   // vSync off → targetFrameRate is respected
            Application.targetFrameRate = targetFps;
            Debug.Log($"[SceneFrameRate] targetFrameRate = {targetFps} for '{gameObject.scene.name}'.");
        }
    }
}
