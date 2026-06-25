using System.Collections;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Fixes a Canvas that stays BLANK until the first screen touch — a known Unity first-frame timing issue where
    /// the canvas mesh isn't built/drawn until an input event forces a canvas update (worst on World Space /
    /// Screen Space - Camera canvases, and with the new Input System). On enable it does what that first touch does:
    /// (a) assigns a missing Event/Render Camera, and (b) force-rebuilds the canvas over the first few frames so it
    /// appears immediately. Drop it on each affected Canvas (e.g. BGCanvas and the world-space canvas in CarouselRoot).
    /// Harmless once the canvas already renders — it just nudges the first frames.
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    [DisallowMultipleComponent]
    public sealed class CanvasWarmup : MonoBehaviour
    {
        [Tooltip("Camera to assign as the canvas Event/Render Camera if it's missing (World Space / Screen Space - " +
                 "Camera only). Leave empty to use Camera.main.")]
        [SerializeField] private Camera renderCamera;

        [Tooltip("How many frames to keep force-rebuilding — covers the intermittent first-frame race. 2–4 is plenty.")]
        [SerializeField] private int warmupFrames = 3;

        private Canvas _canvas;

        private void Awake() => _canvas = GetComponent<Canvas>();

        private void OnEnable() => StartCoroutine(Warmup());

        private IEnumerator Warmup()
        {
            // (a) Assign a missing render/event camera — a null camera on a World/Camera-space canvas is a common cause.
            var cam = renderCamera != null ? renderCamera : Camera.main;
            if (_canvas != null && cam != null && _canvas.worldCamera == null &&
                (_canvas.renderMode == RenderMode.WorldSpace || _canvas.renderMode == RenderMode.ScreenSpaceCamera))
                _canvas.worldCamera = cam;

            // (b) Force the canvas to build + draw NOW instead of waiting for the first input event. A few frames
            // covers the intermittent variant; the off→on toggle (within one frame, so no flicker) is the hard kick.
            for (int i = 0; i < Mathf.Max(1, warmupFrames); i++)
            {
                Canvas.ForceUpdateCanvases();
                yield return null;
            }
            if (_canvas != null) { _canvas.enabled = false; _canvas.enabled = true; }
        }
    }
}
