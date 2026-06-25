using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PlayCard.Home
{
    /// <summary>
    /// KamaGames/Blackjackist-style ring carousel. Its <see cref="ICarouselItem"/> children are placed at
    /// their wrapped offset from the current selection, so the chosen one sits centred with neighbours peeking
    /// on BOTH sides and the far one shrinks away at the back (hiding the wrap). Drive it by swipe or Prev/Next;
    /// only the centred item shows its buttons (via SetSelected).
    ///
    /// Reused by the Home game tiles (placed in-scene) and the Lobby table cards (spawned at runtime — call
    /// <see cref="Rebuild"/> after spawning). Runs in edit mode (<see cref="ExecuteAlways"/>) for camera framing.
    /// </summary>
    [ExecuteAlways]
    public sealed class CarouselController : MonoBehaviour
    {
        [Header("Layout (world units)")]
        [Tooltip("X gap between adjacent items.")]
        [SerializeField] private float spacing = 6f;
        [Tooltip("How far back (Z) an item sits per slot away from centre.")]
        [SerializeField] private float sideDepth = 2f;
        [SerializeField] private float focusedScale = 1f;
        [SerializeField] private float sideScale = 0.7f;
        [Tooltip("Shared Y for every item so the row stays level and grounded on the shadow plane.")]
        [SerializeField] private float itemY = 0f;

        [Header("Motion")]
        [SerializeField] private float slideSmoothTime = 0.25f;
        [Tooltip("Screen pixels of drag that move the ring by one item. Higher = less sensitive.")]
        [Range(150f, 1200f)]
        [SerializeField] private float dragPixelsPerSlot = 600f;

        [Header("Nav (optional)")]
        [SerializeField] private Button prevButton;
        [SerializeField] private Button nextButton;

        /// <summary>Fires with the newly-centred item on every selection change (and once on start).</summary>
        public event Action<ICarouselItem> OnSelectionChanged;

        private readonly List<ICarouselItem> _items = new();
        private int _index;        // unbounded; wraps via modulo so the ring is infinite
        private float _pos, _vel, _dragStartX, _dragStartPos;
        private bool _dragging;
        private bool _needsApply;   // first Apply() is deferred to the first Update — see OnEnable

        public ICarouselItem Current
        {
            get { int n = _items.Count; return n == 0 ? null : _items[((_index % n) + n) % n]; }
        }

        private void OnEnable()
        {
            Collect();
            _pos = _index;
            if (Application.isPlaying)
            {
                if (prevButton) prevButton.onClick.AddListener(Prev);
                if (nextButton) nextButton.onClick.AddListener(Next);
                // Defer the first Apply() to the first Update. Items hide themselves in their OWN Awake
                // (GameMode.SetSelected(false)); since Awake/OnEnable order between us and the items isn't
                // guaranteed, applying NOW can be undone by an item that initialises after us — leaving the centred
                // item hidden until the next selection change (a swipe). The first Update runs AFTER every Awake.
                _needsApply = true;
            }
        }

        private void OnDisable()
        {
            if (prevButton) prevButton.onClick.RemoveListener(Prev);
            if (nextButton) nextButton.onClick.RemoveListener(Next);
        }

        public void Next() { _index++; Apply(); }
        public void Prev() { _index--; Apply(); }

        /// <summary>Re-scan children + re-centre. Call after spawning items at runtime (the Lobby does this).</summary>
        public void Rebuild()
        {
            Collect();
            _index = 0;
            _pos = 0f;
            Apply();
        }

        private void Apply()
        {
            var current = Current;
            // Only the centred item reveals its buttons.
            foreach (var item in _items)
                item?.SetSelected(item == current);
            OnSelectionChanged?.Invoke(current);
        }

        private void Update()
        {
            if (_items.Count != transform.childCount) Collect();
            int n = _items.Count;
            if (n == 0) return;

            if (Application.isPlaying)
            {
                if (_needsApply) { _needsApply = false; Apply(); }   // assert the centred item now all items have initialised
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

                var tt = _items[i].Transform;
                if (!tt) continue;
                var lp = tt.localPosition;
                lp.x = off * spacing;
                lp.y = itemY;
                lp.z = a * sideDepth;
                tt.localPosition = lp;
                tt.localScale = Vector3.one * Mathf.Max(0f, scale);
            }
        }

        private void Collect()
        {
            _items.Clear();
            for (int i = 0; i < transform.childCount; i++)
            {
                var item = transform.GetChild(i).GetComponent<ICarouselItem>();
                if (item != null) _items.Add(item);
            }
        }

        private void HandleSwipe()
        {
            // New Input System: Pointer.current unifies mouse (editor) and touch (device). Ignore drags
            // that begin on UI (so tapping an item's button doesn't also swipe the ring).
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
                // Live drag: the ring follows the finger (drag right → previous item comes to centre).
                float dx = pointer.position.ReadValue().x - _dragStartX;
                _pos = _dragStartPos - dx / Mathf.Max(1f, dragPixelsPerSlot);
            }
            else if (_dragging && pointer.press.wasReleasedThisFrame)
            {
                _dragging = false;
                _index = Mathf.RoundToInt(_pos);   // snap to the nearest item, then SmoothDamp eases in
                Apply();
            }
        }
    }
}
