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
        [Tooltip("Additive glint OVERLAY material (shader Khela/ChipSheen) layered onto the spawned rail chips only. " +
                 "Leave empty for no shine. The chip's own material is left untouched.")]
        [SerializeField] private Material sheenOverlay;
        [Tooltip("Name of the per-slot glow object (a child of the SAME parent as that slot's template chip, e.g. " +
                 "'GlowParticle' under Slot_1). Found automatically per slot and switched on ONLY while that slot " +
                 "actually holds a chip, so an empty slot never glows. Clear this to disable the whole feature.")]
        [SerializeField] private string glowChildName = "GlowParticle";

        private struct Slot
        {
            public Transform parent; public Vector3 pos; public Quaternion rot; public Vector3 scale;
            public GameObject glow;   // that slot's glow, lit only when a chip is spawned into it
        }

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
                _slots[i] = new Slot
                {
                    parent = t.parent,
                    pos = t.localPosition,
                    rot = t.localRotation,
                    scale = t.localScale,
                    // The glow lives beside the template under the same slot object. Found by name so adding one is
                    // pure scene work — no extra array to keep in step with the templates.
                    glow = FindGlow(t.parent),
                };
                if (c.gameObject.activeSelf) c.gameObject.SetActive(false);   // template — never shown
                SetGlow(_slots[i].glow, false);                               // dark until a chip actually spawns here
            }
            _cached = true;
        }

        // The glow object under a slot, or null. Searched INACTIVE-inclusive so it's still found when authored off.
        private GameObject FindGlow(Transform slotParent)
        {
            if (slotParent == null || string.IsNullOrEmpty(glowChildName)) return null;
            foreach (Transform child in slotParent)
                if (child.name == glowChildName) return child.gameObject;
            return null;
        }

        private static void SetGlow(GameObject glow, bool on)
        {
            if (glow != null && glow.activeSelf != on) glow.SetActive(on);
        }

        /// <summary>
        /// Rebuild the rail: spawn <c>prefabs[i]</c> on template <c>i</c> and stamp <c>values[i]</c>, up to the
        /// smallest of the value, prefab, and template counts. Each chip is a sibling of its (hidden) template with
        /// the template's exact local transform, so it lands precisely where you placed the marker.
        /// </summary>
        /// <param name="prefabs">
        /// Colour prefabs ALIGNED to <paramref name="values"/> — pass <see cref="ChipSet.PrefabsFor"/>, not
        /// <see cref="ChipSet.LevelPrefabs"/>. The denomination window slides up the ladder on richer tables, so the
        /// i-th value is not generally the i-th colour rank.
        /// </param>
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
                if (slot.parent == null || prefab == null) continue;   // missing marker, or not enough colour ranks — stays dark

                var go = Instantiate(prefab, slot.parent);
                go.transform.localPosition = slot.pos;
                go.transform.localRotation = slot.rot;
                go.transform.localScale = slot.scale;

                var chip = go.GetComponentInChildren<ChipView>();
                if (chip != null) chip.SetValue(values[i]);

                if (sheenOverlay != null) ChipSheen.Apply(go, sheenOverlay);   // layer the glint overlay on this rail chip only

                SetGlow(slot.glow, true);   // this slot HAS a chip now — light it
                _spawned.Add(go);
            }
        }

        /// <summary>Destroy every spawned chip (templates stay hidden) and darken every slot glow.</summary>
        public void Clear()
        {
            // Glows off FIRST and unconditionally — the rail is cleared whenever betting closes, so this is what stops
            // an empty rail glowing through the round and the whole round-end ceremony.
            if (_slots != null)
                for (int i = 0; i < _slots.Length; i++) SetGlow(_slots[i].glow, false);

            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Destroy(_spawned[i]);
            _spawned.Clear();
        }
    }
}
