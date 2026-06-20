using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Gives World-Space / Screen-Space-Camera canvases their Event Camera at runtime. A prefab can't reference a
    /// scene camera, so a world-space canvas spawned from a prefab has <c>worldCamera == null</c> and receives no
    /// UI clicks (the GraphicRaycaster can't project taps). Put this on the card root (or the canvas) — it sets
    /// the camera on every non-overlay Canvas beneath it. Leave <see cref="cam"/> empty to use <c>Camera.main</c>.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class WorldCanvasCamera : MonoBehaviour
    {
        [Tooltip("Camera used for UI raycasts on the child canvases. Empty = Camera.main (must be tagged MainCamera).")]
        [SerializeField] private Camera cam;

        private void Awake()    => Apply();
        private void OnEnable() => Apply();

        private void Apply()
        {
            var c = cam != null ? cam : Camera.main;
            if (c == null) return;
            foreach (var canvas in GetComponentsInChildren<Canvas>(includeInactive: true))
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    canvas.worldCamera = c;
        }
    }
}
