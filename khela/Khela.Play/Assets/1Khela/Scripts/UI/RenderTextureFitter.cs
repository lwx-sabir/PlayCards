using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Keeps the avatar-stage RenderTexture's aspect EQUAL to the RawImage that displays it, so the 3D
    /// avatar never stretches when the screen / RawImage aspect changes. A fixed-size RT shown on a
    /// full-screen RawImage distorts (fat on wide screens, thin on narrow ones); this recreates the RT at
    /// the RawImage's live aspect and points both the camera and the RawImage at it.
    ///
    /// Because Unity's camera FOV is VERTICAL, vertical framing stays constant and wider screens simply
    /// reveal more horizontal scene (extra transparent side-margin) — so the avatar keeps its proportions
    /// AND its size across every aspect. Size the avatar via the camera FOV/distance; it no longer depends
    /// on aspect, which also kills the "hard to match size" problem.
    ///
    /// WIRING: put this on the RawImage that shows the avatar, assign <see cref="sourceCamera"/> = StageCamera.
    /// It fully owns camera.targetTexture + rawImage.texture at runtime (and in-editor via ExecuteAlways for
    /// WYSIWYG), so you can clear the manually-assigned Avatar RT — it's recreated automatically.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(RawImage))]
    public sealed class RenderTextureFitter : MonoBehaviour
    {
        [Tooltip("The camera that renders the avatar stage (StageCamera).")]
        [SerializeField] private Camera sourceCamera;

        [Tooltip("Vertical resolution of the RT (px). Width is derived from the RawImage aspect. " +
                 "Raise for crisper, lower for cheaper.")]
        [SerializeField] private int referenceHeight = 1440;

        [Tooltip("Hard cap on RT width (px) so ultra-wide aspects can't allocate a huge texture.")]
        [SerializeField] private int maxWidth = 4096;

        [Tooltip("MSAA samples (1 = off, 2/4/8). Smooths the avatar's silhouette edges.")]
        [SerializeField] private int antiAliasing = 2;

        private RawImage _image;
        private RenderTexture _rt;

        private void OnEnable()
        {
            _image = GetComponent<RawImage>();
            Apply();
        }

        private void OnDisable() => ReleaseRT();

        // Fires whenever this RawImage's rect changes (resolution / orientation / layout) — re-fit.
        private void OnRectTransformDimensionsChange()
        {
            if (isActiveAndEnabled) Apply();
        }

        private void Apply()
        {
            if (_image == null) _image = GetComponent<RawImage>();
            if (sourceCamera == null || _image == null) return;

            Rect r = _image.rectTransform.rect;
            if (r.width <= 1f || r.height <= 1f) return;   // not laid out yet

            float aspect = r.width / r.height;
            int h = Mathf.Max(16, referenceHeight);
            int w = Mathf.Clamp(Mathf.RoundToInt(h * aspect), 16, Mathf.Max(16, maxWidth));

            if (_rt != null && _rt.width == w && _rt.height == h) return;   // aspect unchanged — keep it

            ReleaseRT();

            // 24-bit depth is REQUIRED so the 3D scene z-tests and the spot shadow renders; ARGB32 keeps the
            // alpha channel that composites the avatar + shadow over the transparent (alpha-0) background.
            _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
            {
                name = "AvatarRT (auto-fit)",
                antiAliasing = SnapAA(antiAliasing),
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
            };
            _rt.Create();

            sourceCamera.targetTexture = _rt;
            _image.texture = _rt;
        }

        private void ReleaseRT()
        {
            if (_rt == null) return;
            if (sourceCamera != null && sourceCamera.targetTexture == _rt) sourceCamera.targetTexture = null;
            if (_image != null && _image.texture == _rt) _image.texture = null;
            _rt.Release();
            if (Application.isPlaying) Destroy(_rt); else DestroyImmediate(_rt);
            _rt = null;
        }

        // RenderTexture MSAA only accepts 1/2/4/8.
        private static int SnapAA(int aa) => (aa == 2 || aa == 4 || aa == 8) ? aa : 1;
    }
}
