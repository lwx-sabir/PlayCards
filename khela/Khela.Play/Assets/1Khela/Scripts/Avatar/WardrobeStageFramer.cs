using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Frames the wardrobe character at a fixed spot on screen so the HUD can own the rest. Put this on the camera that
    /// renders the character; it keeps a focus point (the character + <see cref="focusOffset"/>) pinned at
    /// <see cref="screenPos"/> (normalized 0–1, 0.5,0.5 = centre) at a chosen <see cref="distance"/>/zoom, using the
    /// camera's OWN rotation as the viewing angle. So: rotate the camera to face the character's front, then slide
    /// <see cref="screenPos"/> to push them to the right (e.g. x = 0.72) and leave the left for panels.
    ///
    /// Runs in edit mode (<see cref="ExecuteAlways"/>) so you position it live in the Scene/Game view without pressing
    /// Play. Works for a direct scene render OR a render-to-texture stage (aspect follows the camera's target).
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class WardrobeStageFramer : MonoBehaviour
    {
        [Tooltip("The character root to frame (the BSMC actor / OutfitSystem transform).")]
        [SerializeField] private Transform target;

        [Tooltip("World-space offset from the target to the point we actually centre on — raise it to frame the chest/face " +
                 "rather than the feet (e.g. Y ≈ 1.0 for a full body, higher to crop to a portrait).")]
        [SerializeField] private Vector3 focusOffset = new Vector3(0f, 1.0f, 0f);

        [Tooltip("Where the focus point sits on screen, normalized. (0.5,0.5) = centre; x>0.5 pushes the character RIGHT. " +
                 "Default 0.72 leaves the left ~two-thirds for the wardrobe panels.")]
        [SerializeField] private Vector2 screenPos = new Vector2(0.72f, 0.5f);

        [Tooltip("Camera-to-focus distance. Perspective: acts as zoom (nearer = bigger). Orthographic: only positions; " +
                 "use Orthographic Size for zoom.")]
        [SerializeField] private float distance = 3.2f;

        [Tooltip("Re-frame every frame (needed if the character or camera moves). Off = only when values change / on enable.")]
        [SerializeField] private bool continuous = true;

        private Camera _cam;

        private Camera Cam => _cam != null ? _cam : (_cam = GetComponent<Camera>());

        private void OnEnable() => Frame();
        private void OnValidate() { if (isActiveAndEnabled) Frame(); }
        private void LateUpdate() { if (continuous) Frame(); }

        /// <summary>Position the camera so <c>target + focusOffset</c> lands at <see cref="screenPos"/> at <see cref="distance"/>.</summary>
        public void Frame()
        {
            if (target == null || Cam == null) return;

            var rot = transform.rotation;                 // the camera's aim IS the viewing angle
            Vector3 f = rot * Vector3.forward;
            Vector3 r = rot * Vector3.right;
            Vector3 u = rot * Vector3.up;

            // Half-extents of the view at the focus depth (vertical, then horizontal via aspect).
            float aspect = Cam.aspect;
            float halfV = Cam.orthographic ? Cam.orthographicSize
                                           : distance * Mathf.Tan(Cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfH = halfV * aspect;

            Vector3 focus = target.position + focusOffset;
            // Back off along -forward by the distance, then shift opposite to where we want the point on screen.
            Vector3 pos = focus
                        - f * distance
                        - r * ((screenPos.x - 0.5f) * 2f * halfH)
                        - u * ((screenPos.y - 0.5f) * 2f * halfV);

            transform.position = pos;
        }

        private void OnDrawGizmosSelected()
        {
            if (target == null) return;
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireSphere(target.position + focusOffset, 0.06f);
        }
    }
}
