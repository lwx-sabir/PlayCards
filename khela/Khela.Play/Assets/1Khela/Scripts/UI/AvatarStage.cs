using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Hosts the 3D character shown in the profile's centre. The character lives in an isolated "stage" (its own
    /// layer + light + camera) that an <c>AvatarCamera</c> renders to a RenderTexture; a <c>RawImage</c> in the UI
    /// displays that texture, so the animated 3D model sits exactly inside its panel rect, layered under the UI
    /// overlays, with a transparent background (the blue backdrop + ground shadow are UI behind the RawImage).
    ///
    /// Put this on the stage root (placed FAR from the rest of the scene, e.g. x = 1000, so its lighting doesn't
    /// bleed into Home). Drop the demo character under <see cref="mount"/> directly for now; later call
    /// <see cref="Show"/> with the prefab resolved from the profile's AvatarId to swap it. Everything spawned is
    /// forced onto <see cref="avatarLayer"/> so only the AvatarCamera (whose culling mask is that one layer) sees it.
    /// </summary>
    public sealed class AvatarStage : MonoBehaviour
    {
        [Tooltip("Where the character sits — an empty child the camera is framed on. Spawned avatars parent here.")]
        [SerializeField] private Transform mount;

        [Tooltip("Optional: the demo/dummy character prefab to spawn on Start if the mount is empty. Leave null if " +
                 "you've placed the character under the mount by hand.")]
        [SerializeField] private GameObject defaultAvatar;

        [Tooltip("Name of the dedicated 'Avatar' layer. Everything spawned/adopted is forced onto it so ONLY the " +
                 "AvatarCamera (its culling mask) renders it. Must match the layer you added in Tags & Layers.")]
        [SerializeField] private string avatarLayerName = "Avatar";

        [Tooltip("Gentle idle turn, degrees/sec (0 = none). For drag-to-rotate, call AddYaw from your UI drag handler instead.")]
        [SerializeField] private float autoRotateDegPerSec = 0f;

        private GameObject _current;

        public GameObject Current => _current;

        private void Start()
        {
            // If nothing was placed under the mount by hand, spawn the demo avatar.
            if (_current == null && defaultAvatar != null && mount != null && mount.childCount == 0)
                Show(defaultAvatar);
            else if (_current == null && mount != null && mount.childCount > 0)
            {
                _current = mount.GetChild(0).gameObject;   // adopt a hand-placed character
                ApplyLayer(_current);                      // ...and force it onto the avatar layer so the camera sees it
            }
        }

        /// <summary>Spawn/swap the displayed character. Instantiated under the mount and forced onto the avatar layer.</summary>
        public void Show(GameObject prefab)
        {
            if (mount == null || prefab == null) return;
            if (_current != null) Destroy(_current);
            _current = Instantiate(prefab, mount);
            _current.transform.localPosition = Vector3.zero;
            _current.transform.localRotation = Quaternion.identity;
            ApplyLayer(_current);
        }

        /// <summary>Spin the character (e.g. from a drag handler over the avatar area). Degrees about world up.</summary>
        public void AddYaw(float degrees)
        {
            if (_current != null) _current.transform.Rotate(0f, degrees, 0f, Space.World);
        }

        private void Update()
        {
            if (autoRotateDegPerSec != 0f && _current != null)
                _current.transform.Rotate(0f, autoRotateDegPerSec * Time.deltaTime, 0f, Space.World);
        }

        private void ApplyLayer(GameObject go)
        {
            int layer = LayerMask.NameToLayer(avatarLayerName);
            if (layer < 0) { Debug.LogWarning($"[AvatarStage] layer '{avatarLayerName}' not found — add it in Project Settings ▸ Tags and Layers."); return; }
            SetLayerRecursive(go, layer);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayerRecursive(t.gameObject, layer);
        }
    }
}
