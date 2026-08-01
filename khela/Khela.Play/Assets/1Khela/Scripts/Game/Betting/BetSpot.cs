using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Per-seat drop target + landing pad for one seat's bet. Its <see cref="BoxCollider"/> (on the drop layer)
    /// both detects the chip drop and defines where chips land; on <see cref="Awake"/> it builds ONE invisible
    /// floor under that box. There are deliberately NO walls — the drop is already constrained to the box area, and
    /// walls just make border chips lean on an invisible surface and read as a bug. A released chip is given a
    /// Rigidbody + convex collider, dropped in from a little above where you let go, and left to fall and settle
    /// with full physics — chips pile up naturally. The pile clears when the bet resets (Clear / Deal).
    /// <see cref="BetSpots"/> enables only the local player's spot.
    ///
    /// Keep the spot's transform scale at (1,1,1) so the chips don't get rescaled when they drop in.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public sealed class BetSpot : MonoBehaviour
    {
        [SerializeField] private BetBuilder builder;
        [Tooltip("1-based seat this spot sits in front of. BetSpots enables only the local player's spot.")]
        [SerializeField] private int seatNumber = 1;

        [Header("Physics drop")]
        [Tooltip("Fallback felt scale, used only if no BetStacks/ChipSet is present. The real value lives on the " +
                 "ChipSet asset (Felt Chip Scale) so a dropped chip and a stacked one can never disagree.")]
        [SerializeField] private float droppedChipScaleFallback = 1.2f;
        [Tooltip("How high above the box a chip appears before it falls.")]
        [SerializeField] private float dropHeight = 0.2f;
        [Tooltip("Thickness of the invisible floor the chips land on (world units).")]
        [SerializeField] private float floorThickness = 0.01f;
        [Tooltip("How far the floor extends past the drop box on each side — a small lip so an edge chip can't tip off the table. No walls.")]
        [SerializeField] private float floorMargin = 0.05f;
        [Tooltip("Mass of a dropped chip's Rigidbody.")]
        [SerializeField] private float chipMass = 0.02f;
        [Tooltip("Physics layer for dropped chips + the auto-built tray. Keep it OFF the drop layer AND the rail's " +
                 "Chip layer, so settled chips never catch the drop ray or get re-picked.")]
        [SerializeField] private int physicsLayer = 0;   // Default

        private BoxCollider _box;
        private readonly List<GameObject> _chips = new List<GameObject>();

        /// <summary>1-based seat this spot belongs to.</summary>
        public int SeatNumber => seatNumber;

        private void Awake()
        {
            _box = GetComponent<BoxCollider>();
            _box.isTrigger = true;          // detection + landing area only; the floor does the physics
            BuildFloor();
        }

        private void OnEnable()  { if (builder != null) builder.OnBetChanged += OnBet; }
        private void OnDisable() { if (builder != null) builder.OnBetChanged -= OnBet; }

        // Bet went to zero (Clear or Deal) → wipe the pile.
        private void OnBet(decimal total) { if (total == 0m) Clear(); }

        /// <summary>Allow/deny chip drops here (detection collider only; the walls stay).</summary>
        public void SetAccepting(bool on)
        {
            if (_box == null) _box = GetComponent<BoxCollider>();
            if (_box != null) _box.enabled = on;
        }

        /// <summary>
        /// Drop a released chip into the tray with physics: re-enable its collider (convex, so a Rigidbody is
        /// legal), add a Rigidbody, place it a little above where it was released (clamped inside the walls), and
        /// let it fall and settle. Called by <see cref="ChipDragController"/>.
        /// </summary>
        public void Stack(GameObject chip)
        {
            if (chip == null) return;
            if (_box == null) _box = GetComponent<BoxCollider>();

            ChipSheen.Remove(chip);   // it's a copy of a rail chip — drop the rail glint once it settles into the bet

            // Give the dropped chip a physics-legal collider. Its authored collider is a MeshCollider using an
            // import-LOCKED (non-readable) collision mesh (e.g. "Coin_1_Collision"): a convex MeshCollider on a dynamic
            // Rigidbody must BAKE that mesh at runtime, which needs its Read/Write flag — so it throws
            // ("CollisionMeshData couldn't be created … marked as non-accessible") and the whole drop aborts (chips
            // never appear). Swap any MeshCollider for a cheap BoxCollider sized from the mesh's AABB — Mesh.bounds is
            // accessible without Read/Write, there's no bake, and a primitive is faster to simulate anyway.
            SetLayerRecursive(chip, physicsLayer);
            foreach (var c in chip.GetComponentsInChildren<Collider>(true))
            {
                if (c is MeshCollider mc)
                {
                    mc.enabled = false;   // inert: the box below does the physics (no convex-bake, no read error)
                    var go = mc.gameObject;
                    if (go.GetComponent<BoxCollider>() == null)
                    {
                        var box = go.AddComponent<BoxCollider>();   // enabled + solid by default
                        var src = mc.sharedMesh != null ? mc.sharedMesh
                                : go.GetComponentInChildren<MeshFilter>(true)?.sharedMesh;
                        if (src != null) { box.center = src.bounds.center; box.size = src.bounds.size; }
                    }
                }
                else
                {
                    c.enabled = true;
                    c.isTrigger = false;
                }
            }

            // Position it above the tray (release XZ clamped inside the walls) BEFORE adding the body — a fresh
            // Rigidbody then starts cleanly at the spawn pose, so there's no 1-frame interpolation "flicker" from
            // the release point (which otherwise reads as a teleport instead of a drop).
            chip.transform.SetParent(transform, worldPositionStays: true);

            // Felt size. The dropped chip is a clone of a RAIL chip, so it carries that rail template's scale — sized
            // for the rail's own camera view, not for the table. Set it AFTER the reparent:
            // SetParent(worldPositionStays: true) rewrites localScale to preserve the world size, so doing this any
            // earlier would just be undone. (BoxCollider.size is in local space and scales with the transform, so the
            // collider fitted above stays correct — the chip's physics footprint grows with it.)
            chip.transform.localScale = Vector3.one * FeltChipScale();

            Vector3 local = transform.InverseTransformPoint(chip.transform.position);
            Vector3 half = _box.size * 0.5f;
            float pad = 0.01f;   // keep the spawn just inside the visible box outline
            local.x = Mathf.Clamp(local.x, _box.center.x - half.x + pad, _box.center.x + half.x - pad);
            local.z = Mathf.Clamp(local.z, _box.center.z - half.z + pad, _box.center.z + half.z - pad);
            local.y = _box.center.y + half.y + dropHeight;
            chip.transform.localPosition = local;

            var rb = chip.GetComponent<Rigidbody>();
            if (rb == null) rb = chip.AddComponent<Rigidbody>();
            rb.mass = chipMass;
            rb.useGravity = true;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            _chips.Add(chip);
        }

        /// <summary>Destroy the whole pile (templates/tray stay).</summary>
        public void Clear()
        {
            for (int i = 0; i < _chips.Count; i++)
                if (_chips[i] != null) Destroy(_chips[i]);
            _chips.Clear();
        }

        /// <summary>True while dropped chips are resting on the felt (for the gather-on-deal animation).</summary>
        public bool HasChips => _chips.Count > 0;

        /// <summary>
        /// Hand the pile off to a gather animation instead of destroying it: FREEZE each chip (kinematic body,
        /// colliders off) so a tween can pull it cleanly, then return the list and forget it here. Because the list is
        /// emptied WITHOUT destroying, the zero-bet <see cref="Clear"/> that fires right after DEAL becomes a no-op and
        /// the caller (BetStacks) now owns the chips — it pulls them to the stack anchor and adopts them as the
        /// committed stack. The explicit CLEAR/UNDO path is untouched: it still wipes instantly.
        /// </summary>
        public List<GameObject> DetachChips()
        {
            var outList = new List<GameObject>(_chips);
            _chips.Clear();
            for (int i = 0; i < outList.Count; i++)
            {
                var chip = outList[i];
                if (chip == null) continue;
                var rb = chip.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;                 // stop physics; the tween drives it now
                foreach (var c in chip.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            }
            return outList;
        }

        // Build ONE invisible floor (slightly larger than the drop box) so chips land and rest. No walls — the drop
        // box already constrains where chips land, and walls just make border chips lean on an invisible surface.
        private void BuildFloor()
        {
            Vector3 c = _box.center, s = _box.size, half = s * 0.5f;
            AddBox("Floor", new Vector3(c.x, c.y - half.y, c.z),
                   new Vector3(s.x + floorMargin * 2f, floorThickness, s.z + floorMargin * 2f));
        }

        private void AddBox(string label, Vector3 localCenter, Vector3 size)
        {
            var go = new GameObject(label) { layer = physicsLayer };
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = localCenter;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.AddComponent<BoxCollider>().size = size;
        }

        // The felt chip size, taken from the shared ChipSet via BetStacks so a dropped chip is exactly the size of the
        // stacked chips it will merge into. Cached — the lookup is scene-wide and the answer can't change at runtime.
        private BetStacks _stacks;
        private bool _lookedForStacks;

        private float FeltChipScale()
        {
            if (!_lookedForStacks)
            {
                _lookedForStacks = true;
                _stacks = FindAnyObjectByType<BetStacks>(FindObjectsInactive.Include);
            }
            return _stacks != null ? _stacks.FeltChipScale : droppedChipScaleFallback;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayerRecursive(t.gameObject, layer);
        }
    }
}
