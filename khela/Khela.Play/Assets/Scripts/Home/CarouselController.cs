using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PlayCard.Home
{
    /// <summary>
    /// KamaGames/Blackjackist-style game carousel — a circular ring of game modes (each a prefab). The modes
    /// are the direct children of this root; each frame every mode is placed at its wrapped offset from the
    /// current selection, so the chosen mode sits centred with neighbours peeking on BOTH sides, and the far
    /// one shrinks away at the back (hiding the wrap). Drive it by swipe or Prev/Next.
    ///
    /// Each mode owns its own title + Play Now / Lobby buttons (see <see cref="GameMode"/>); the carousel just
    /// tells the centred one to reveal its buttons via SetSelected. Runs in edit mode
    /// (<see cref="ExecuteAlways"/>) so you can frame the camera without pressing Play.
    /// </summary>
    [ExecuteAlways]
    public sealed class CarouselController : MonoBehaviour
    {
        [Header("Layout (world units)")]
        [Tooltip("X gap between adjacent modes.")]
        [SerializeField] private float spacing = 6f;
        [Tooltip("How far back (Z) a mode sits per slot away from centre.")]
        [SerializeField] private float sideDepth = 2f;
        [SerializeField] private float focusedScale = 1f;
        [SerializeField] private float sideScale = 0.7f;
        [Tooltip("Shared Y for every mode so the row stays level and grounded on the shadow plane.")]
        [SerializeField] private float tableY = 0f;

        [Header("Motion")]
        [SerializeField] private float slideSmoothTime = 0.25f;
        [Tooltip("Screen pixels of drag that move the ring by one mode. Higher = less sensitive.")]
        [Range(150f, 1200f)]
        [SerializeField] private float dragPixelsPerSlot = 600f;

        [Header("Nav (optional)")]
        [SerializeField] private Button prevButton;
        [SerializeField] private Button nextButton;

        /// <summary>Fires with the newly-centred mode on every selection change (and once on start).</summary>
        public event Action<GameMode> OnSelectionChanged;

        private readonly List<GameMode> _modes = new();
        private int _index;        // unbounded; wraps via modulo so the ring is infinite
        private float _pos, _vel, _dragStartX, _dragStartPos;
        private bool _dragging;

        public GameMode Current
        {
            get { int n = _modes.Count; return n == 0 ? null : _modes[((_index % n) + n) % n]; }
        }

        private void OnEnable()
        {
            Collect();
            _pos = _index;
            if (Application.isPlaying)
            {
                if (prevButton) prevButton.onClick.AddListener(Prev);
                if (nextButton) nextButton.onClick.AddListener(Next);
                Apply();
            }
        }

        private void OnDisable()
        {
            if (prevButton) prevButton.onClick.RemoveListener(Prev);
            if (nextButton) nextButton.onClick.RemoveListener(Next);
        }

        public void Next() { _index++; Apply(); }
        public void Prev() { _index--; Apply(); }

        private void Apply()
        {
            var current = Current;
            // Only the centred mode reveals its Play Now / Lobby buttons.
            foreach (var m in _modes)
                if (m) m.SetSelected(m == current);
            OnSelectionChanged?.Invoke(current);
        }

        private void Update()
        {
            if (_modes.Count != transform.childCount) Collect();
            int n = _modes.Count;
            if (n == 0) return;

            if (Application.isPlaying)
            {
                HandleSwipe();
                if (!_dragging)
                    _pos = Mathf.SmoothDamp(_pos, _index, ref _vel, slideSmoothTime);
            }
            else
            {
                _pos = _index;   // snap in edit mode so the ring reflects the inspector
            }

            float half = n / 2f;
            for (int i = 0; i < n; i++)
            {
                float off = Mathf.Repeat(i - _pos + half, n) - half;  // nearest wrapped offset, continuous
                float a = Mathf.Abs(off);

                // Centre = full size & forward; one slot out = sideScale; beyond that shrink to nothing at the
                // back so the loop's wrap-around is invisible (you always see centre + left + right).
                float scale = a <= 1f
                    ? Mathf.Lerp(focusedScale, sideScale, a)
                    : Mathf.Lerp(sideScale, 0f, Mathf.Clamp01(a - 1f));

                var tt = _modes[i].transform;
                var lp = tt.localPosition;
                lp.x = off * spacing;
                lp.y = tableY;
                lp.z = a * sideDepth;
                tt.localPosition = lp;
                tt.localScale = Vector3.one * Mathf.Max(0f, scale);
            }
        }

        private void Collect()
        {
            _modes.Clear();
            for (int i = 0; i < transform.childCount; i++)
            {
                var m = transform.GetChild(i).GetComponent<GameMode>();
                if (m != null) _modes.Add(m);
            }
        }

        private void HandleSwipe()
        {
            // New Input System: Pointer.current unifies mouse (editor) and touch (device). Ignore drags
            // that begin on UI (so tapping a mode's button doesn't also swipe the ring).
            var pointer = Pointer.current;
            if (pointer == null) return;

            if (pointer.press.wasPressedThisFrame)
            {
                if (EventSystem.current && EventSystem.current.IsPointerOverGameObject()) return;
                _dragging = true;
                _dragStartX = pointer.position.ReadValue().x;
                _dragStartPos = _pos;
            }
            else if (_dragging && pointer.press.isPressed)
            {
                // Live drag: the ring follows the finger (drag right → previous mode comes to centre).
                float dx = pointer.position.ReadValue().x - _dragStartX;
                _pos = _dragStartPos - dx / Mathf.Max(1f, dragPixelsPerSlot);
            }
            else if (_dragging && pointer.press.wasReleasedThisFrame)
            {
                _dragging = false;
                _index = Mathf.RoundToInt(_pos);   // snap to the nearest mode, then SmoothDamp eases in
                Apply();
            }
        }
    }
}
