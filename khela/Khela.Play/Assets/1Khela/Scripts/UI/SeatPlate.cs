using PlayCard.Game.Dtos;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One seat's floating name plate (avatar + name + chips; room for rank/star later). It is screen-space UI
    /// that FOLLOWS its seat's world anchor by projecting that point to screen each frame — so it rides correctly
    /// under the per-seat cameras and never tilts or distorts. A <see cref="SeatPlates"/> driver shows/hides and
    /// fills it from the live board.
    ///
    /// Put this on the plate root (anchor it center: anchorMin = anchorMax = 0.5). Assign the seat's world anchor
    /// (an empty at the chair / above the head). The avatar Image is left to <see cref="SetAvatar"/> (profile pic
    /// from FB/chosen) once that's wired — the board carries only name + chips today.
    /// </summary>
    public sealed class SeatPlate : MonoBehaviour
    {
        [Tooltip("1-based seat number this plate represents.")]
        [SerializeField] private int seatNumber = 1;
        [Tooltip("World point to hover over — an empty at the seat / above the player's head.")]
        [SerializeField] private Transform worldAnchor;
        [Tooltip("Visual to show/hide for occupied vs empty. Defaults to this GameObject.")]
        [SerializeField] private GameObject content;

        [Header("Fields")]
        [SerializeField] private Image avatar;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text chipsText;
        [SerializeField] private string chipsFormat = "#,0";

        [Header("Follow")]
        [Tooltip("Scene camera used to project the anchor. Empty = Camera.main (the moving table camera).")]
        [SerializeField] private Camera cam;
        [Tooltip("Pixel nudge from the anchor's screen point (e.g. y+ to sit above the head).")]
        [SerializeField] private Vector2 screenOffset;

        public int SeatNumber => seatNumber;

        private static readonly Vector2 OffScreen = new Vector2(-99999f, -99999f);

        private RectTransform _rt;
        private RectTransform _parentRect;
        private Canvas _canvas;
        private bool _shown;

        private void Awake()
        {
            _rt = (RectTransform)transform;
            _parentRect = _rt.parent as RectTransform;
            _canvas = GetComponentInParent<Canvas>();
            if (content == null) content = gameObject;
            Hide();
        }

        /// <summary>Bind + show an occupied seat.</summary>
        public void Show(PlayerView p)
        {
            if (p == null) { Hide(); return; }
            _shown = true;
            if (content && !content.activeSelf) content.SetActive(true);
            if (nameText) nameText.text = p.Name;
            if (chipsText) chipsText.text = p.Balance.ToString(chipsFormat);
            // avatar comes from the player's profile pic (FB/chosen) — set via SetAvatar when that's wired.
        }

        /// <summary>Empty seat — hide the plate.</summary>
        public void Hide()
        {
            _shown = false;
            if (content && content.activeSelf) content.SetActive(false);
        }

        /// <summary>Drop in the profile sprite (FB / chosen) once available.</summary>
        public void SetAvatar(Sprite sprite)
        {
            if (avatar && sprite) avatar.sprite = sprite;
        }

        private void LateUpdate()
        {
            if (!_shown || worldAnchor == null || _rt == null) return;

            var c = cam != null ? cam : Camera.main;
            if (c == null) return;

            Vector3 sp = c.WorldToScreenPoint(worldAnchor.position);
            if (sp.z < 0f) { _rt.anchoredPosition = OffScreen; return; }   // anchor behind camera → park off-screen

            // Robust for both Screen-Space-Overlay (uiCam = null) and Screen-Space-Camera canvases.
            var uiCam = (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                ? _canvas.worldCamera
                : null;

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_parentRect, sp, uiCam, out var local))
                _rt.anchoredPosition = local + screenOffset;
        }
    }
}
