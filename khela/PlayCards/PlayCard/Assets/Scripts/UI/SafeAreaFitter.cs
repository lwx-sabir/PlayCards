using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Insets a RectTransform to the device safe area (notch, punch-hole, rounded corners, the iOS
    /// home indicator). Put it on a full-screen child of the Canvas and parent your UI under it.
    /// Re-applies automatically when orientation or resolution changes.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        private RectTransform _rt;
        private Rect _lastSafe;
        private ScreenOrientation _lastOrientation;
        private Vector2Int _lastResolution;

        private void Awake() => _rt = GetComponent<RectTransform>();

        private void OnEnable() => Apply(force: true);

        private void Update()
        {
            // Cheap guard: only recompute when something actually changed.
            if (Screen.safeArea != _lastSafe
                || Screen.orientation != _lastOrientation
                || Screen.width != _lastResolution.x
                || Screen.height != _lastResolution.y)
            {
                Apply(force: false);
            }
        }

        private void Apply(bool force)
        {
            if (_rt == null) return;
            if (Screen.width <= 0 || Screen.height <= 0) return;

            Rect safe = Screen.safeArea;

            Vector2 anchorMin = safe.position;
            Vector2 anchorMax = safe.position + safe.size;
            anchorMin.x /= Screen.width;
            anchorMin.y /= Screen.height;
            anchorMax.x /= Screen.width;
            anchorMax.y /= Screen.height;

            // Ignore transient bad values during a rotation frame.
            if (anchorMin.x < 0f || anchorMin.y < 0f || anchorMax.x > 1f || anchorMax.y > 1f) return;

            _lastSafe = safe;
            _lastOrientation = Screen.orientation;
            _lastResolution = new Vector2Int(Screen.width, Screen.height);

            _rt.anchorMin = anchorMin;
            _rt.anchorMax = anchorMax;
            _rt.offsetMin = Vector2.zero;
            _rt.offsetMax = Vector2.zero;
        }
    }
}
