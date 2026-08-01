using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Fits this RectTransform to the device SAFE AREA — inside notches, punch-hole cameras, rounded corners and the
    /// home-indicator inset. Put it on a FULL-STRETCH child of the HUD Canvas and parent all edge-hugging UI (top bar,
    /// action bar, popups) under it; that content then never lands under a cutout. Full-screen art (the 3D table shows
    /// through the camera, plus any background/dim/vignette) stays OUTSIDE so it still bleeds to the physical edges.
    ///
    /// Robust by design: it sets NORMALIZED anchors (0..1) from <see cref="Screen.safeArea"/>, so it's correct for any
    /// CanvasScaler, any device, and both Screen-Space-Overlay and Screen-Space-Camera canvases. It re-applies whenever
    /// the safe area or resolution changes (rotate, split view, the editor Device Simulator swapping devices), so you
    /// don't have to think about it again. Per-edge flags let a bottom bar hug only the home-indicator inset while a
    /// full-screen panel conforms all four sides.
    ///
    /// Runtime only on purpose (no [ExecuteAlways]): in the editor the panel stays full-stretch so you AUTHOR against the
    /// whole screen; to preview the real insets, use the <b>Device Simulator</b> window in Play mode. Wire scenes with
    /// <c>Khela ▸ UI ▸ Wrap Selection in Safe Area</c>.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public sealed class SafeArea : MonoBehaviour
    {
        [Header("Edges to inset (uncheck to let content bleed to that screen edge)")]
        [SerializeField] private bool left = true;
        [SerializeField] private bool right = true;
        [SerializeField] private bool top = true;
        [SerializeField] private bool bottom = true;

        private RectTransform _rt;
        private Rect _lastSafe;
        private Vector2Int _lastScreen = new Vector2Int(-1, -1);

        private void Awake() => _rt = GetComponent<RectTransform>();

        private void OnEnable()
        {
            if (_rt == null) _rt = GetComponent<RectTransform>();
            _lastScreen = new Vector2Int(-1, -1);   // force a re-apply on (re)enable / scene load
            Apply();
        }

        private void Update()
        {
            // The safe area or resolution can change with NO callback (rotate, split view, sim device swap), so poll.
            // The comparison is a couple of struct checks — cheap enough to run every frame, and it early-outs instantly.
            if (Screen.safeArea != _lastSafe || Screen.width != _lastScreen.x || Screen.height != _lastScreen.y)
                Apply();
        }

        private void Apply()
        {
            if (_rt == null) return;

            int w = Screen.width, h = Screen.height;
            if (w <= 0 || h <= 0) return;   // no valid screen yet (first frame on some platforms)

            var safe = Screen.safeArea;
            _lastSafe = safe;
            _lastScreen = new Vector2Int(w, h);

            // Normalize the safe rect (pixels) to 0..1 anchor space. Edges we don't conform snap back to the full screen.
            Vector2 min = safe.position;
            Vector2 max = safe.position + safe.size;
            min.x /= w; max.x /= w;
            min.y /= h; max.y /= h;

            if (!left)   min.x = 0f;
            if (!right)  max.x = 1f;
            if (!bottom) min.y = 0f;
            if (!top)    max.y = 1f;

            // Guard a bad read: some platforms report an empty/zero safe area for a frame at startup. Don't collapse the UI.
            if (max.x - min.x <= 0f || max.y - min.y <= 0f) return;

            _rt.anchorMin = min;
            _rt.anchorMax = max;
            _rt.offsetMin = Vector2.zero;
            _rt.offsetMax = Vector2.zero;
        }
    }
}
