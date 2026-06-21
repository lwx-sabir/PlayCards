using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// One seat's chip rail. The chips you place by hand are TEMPLATES ONLY — markers for exactly where, and
    /// facing how, each chip sits for THIS seat's camera view (low → high). They are hidden before the table
    /// renders (cached + disabled in <see cref="Awake"/>, even if you left one enabled), and the REAL chips are
    /// spawned onto their transforms at runtime by <see cref="ChipRailSpawner"/>. The spawned colour and count
    /// come from the table (via the ChipSet), NOT from whatever colour you happened to place.
    ///
    /// Each real chip is a sibling of its template with the template's exact local transform, so there is zero
    /// pivot/offset drift. Place up to 6 templates (the 6th only fills on bigger tables); a template with no
    /// matching chip this round simply stays hidden.
    /// </summary>
    public sealed class ChipRail : MonoBehaviour
    {
        [Tooltip("Template chips for this seat's view, low → high (cheapest first). MARKERS ONLY — hidden at " +
                 "runtime; the real chips spawn onto them. The spawned colour comes from the ChipSet, not these.")]
        [SerializeField] private ChipView[] chips;

        private struct Slot { public Transform parent; public Vector3 pos; public Quaternion rot; public Vector3 scale; }

        private Slot[] _slots;
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private bool _cached;

        /// <summary>How many chips this rail can show (its template count).</summary>
        public int Capacity => chips != null ? chips.Length : 0;

        private void Awake() => Cache();

        // Record each template's placement, then hide it so a marker never renders (even if it was left enabled).
        private void Cache()
        {
            if (_cached || chips == null) return;
            _slots = new Slot[chips.Length];
            for (int i = 0; i < chips.Length; i++)
            {
                var c = chips[i];
                if (c == null) continue;
                var t = c.transform;
                _slots[i] = new Slot { parent = t.parent, pos = t.localPosition, rot = t.localRotation, scale = t.localScale };
                if (c.gameObject.activeSelf) c.gameObject.SetActive(false);   // template — never shown
            }
            _cached = true;
        }

        /// <summary>
        /// Rebuild the rail: spawn <c>prefabs[i]</c> on template <c>i</c> and stamp <c>values[i]</c>, up to the
        /// smallest of the value, prefab, and template counts. Each chip is a sibling of its (hidden) template with
        /// the template's exact local transform, so it lands precisely where you placed the marker.
        /// </summary>
        public void Spawn(IReadOnlyList<long> values, IReadOnlyList<GameObject> prefabs)
        {
            Cache();
            Clear();
            if (_slots == null || values == null || prefabs == null) return;

            int n = Mathf.Min(_slots.Length, values.Count);
            for (int i = 0; i < n; i++)
            {
                var slot = _slots[i];
                var prefab = i < prefabs.Count ? prefabs[i] : null;
                if (slot.parent == null || prefab == null) continue;   // missing marker, or not enough colour ranks

                var go = Instantiate(prefab, slot.parent);
                go.transform.localPosition = slot.pos;
                go.transform.localRotation = slot.rot;
                go.transform.localScale = slot.scale;

                var chip = go.GetComponentInChildren<ChipView>();
                if (chip != null) chip.SetValue(values[i]);

                _spawned.Add(go);
            }
        }

        /// <summary>Destroy every spawned chip (templates stay hidden).</summary>
        public void Clear()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Destroy(_spawned[i]);
            _spawned.Clear();
        }
    }
}
