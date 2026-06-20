using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// 3D chip drag-and-drop (new Input System). Press a rail <see cref="ChipView"/> → a ghost copy follows your
    /// finger across the table → release over the <see cref="BetSpot"/> to add that chip's value to the bet
    /// (the ghost stays on the spot's stack); release anywhere else cancels (ghost discarded, rail untouched).
    /// One component on the table camera. Rail chips live on <see cref="chipLayers"/>, the bet spot on
    /// <see cref="dropLayers"/> (use separate layers so the felt collider doesn't block the ray).
    /// </summary>
    public sealed class ChipDragController : MonoBehaviour
    {
        [SerializeField] private Camera cam;
        [SerializeField] private BetBuilder builder;
        [Tooltip("Layer(s) the rail chip colliders are on.")]
        [SerializeField] private LayerMask chipLayers;
        [Tooltip("Layer(s) the bet-spot collider is on.")]
        [SerializeField] private LayerMask dropLayers;
        [SerializeField] private float maxDistance = 100f;

        private ChipView _chip;      // the rail chip being dragged
        private GameObject _ghost;   // the visual following the pointer
        private Plane _plane;        // horizontal drag plane at the chip's height

        private void Awake() { if (cam == null) cam = Camera.main; }

        private void Update()
        {
            var p = Pointer.current;
            if (p == null || cam == null) return;
            var pos = p.position.ReadValue();

            if (p.press.wasPressedThisFrame) Begin(pos);
            else if (_chip != null && p.press.isPressed) Move(pos);
            else if (_chip != null && p.press.wasReleasedThisFrame) End(pos);
        }

        private void Begin(Vector2 screen)
        {
            var ray = cam.ScreenPointToRay(screen);
            if (!Physics.Raycast(ray, out var hit, maxDistance, chipLayers)) return;

            var chip = hit.collider.GetComponentInParent<ChipView>();
            if (chip == null) return;
            if (builder != null && !builder.CanPlace(chip.Value)) return; // unaffordable / over cap → don't pick up

            _chip = chip;
            _plane = new Plane(Vector3.up, hit.point);                    // slide along the table at the chip's height
            _ghost = Instantiate(chip.gameObject, chip.transform.position, chip.transform.rotation);
            foreach (var c in _ghost.GetComponentsInChildren<Collider>()) c.enabled = false; // ghost isn't clickable
            foreach (var b in _ghost.GetComponentsInChildren<ChipView>()) Destroy(b);
        }

        private void Move(Vector2 screen)
        {
            if (_ghost == null) return;
            var ray = cam.ScreenPointToRay(screen);
            if (_plane.Raycast(ray, out float d)) _ghost.transform.position = ray.GetPoint(d);
        }

        private void End(Vector2 screen)
        {
            var ray = cam.ScreenPointToRay(screen);
            var dropped = false;

            if (Physics.Raycast(ray, out var hit, maxDistance, dropLayers))
            {
                var spot = hit.collider.GetComponentInParent<BetSpot>();
                if (spot != null && builder != null && builder.CanPlace(_chip.Value))
                {
                    builder.Add(_chip.Value);
                    spot.Stack(_ghost);   // the ghost becomes the chip sitting on the bet spot
                    _ghost = null;
                    dropped = true;
                }
            }

            if (!dropped && _ghost != null) Destroy(_ghost); // missed the spot (or can't afford) → discard
            _ghost = null;
            _chip = null;
        }
    }
}
