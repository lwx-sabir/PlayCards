using UnityEngine;

namespace PlayCard.Game.Cards
{
    /// <summary>
    /// Eases a card's LOCAL pose (position / rotation / scale) toward a target so deals glide in and round-end
    /// collects glide out, instead of snapping. The table view sets the target each layout pass; this lerps every
    /// frame. <see cref="Target"/> is idempotent — calling it with the same target (as the view does on every
    /// identical board push) does NOT restart the tween, so a card actually settles. Unscaled time, so it animates
    /// even if the game is paused. Auto-added to each card by the view; no wiring.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CardMover : MonoBehaviour
    {
        private Vector3 _fromPos, _toPos, _fromScale, _toScale;
        private Quaternion _fromRot, _toRot;
        private float _start, _dur;
        private bool _animating;

        /// <summary>True while a tween is in flight.</summary>
        public bool Animating => _animating;

        /// <summary>Instantly place the card (no tween) — used to drop it at the deal source before sliding in.</summary>
        public void Snap(Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var t = transform;
            t.localPosition = pos; t.localRotation = rot; t.localScale = scale;
            _toPos = pos; _toRot = rot; _toScale = scale;
            _animating = false;
        }

        /// <summary>Ease toward the target over <paramref name="seconds"/>. No-op restart if the target is unchanged.</summary>
        public void Target(Vector3 pos, Quaternion rot, Vector3 scale, float seconds)
        {
            if (seconds <= 0f) { Snap(pos, rot, scale); return; }

            // Same destination as we're already heading to / already resting at → don't restart (so identical
            // board pushes every tick can't keep the card from ever settling).
            bool sameTarget = Approx(_toPos, pos) && Approx(_toScale, scale) && Quaternion.Angle(_toRot, rot) < 0.5f;
            if (_animating && sameTarget) return;
            if (!_animating && sameTarget &&
                Approx(transform.localPosition, pos) && Quaternion.Angle(transform.localRotation, rot) < 0.5f)
                return;

            var tr = transform;
            _fromPos = tr.localPosition; _fromRot = tr.localRotation; _fromScale = tr.localScale;
            _toPos = pos; _toRot = rot; _toScale = scale;
            _start = Time.unscaledTime; _dur = seconds; _animating = true;
        }

        private void Update()
        {
            if (!_animating) return;
            float u = Mathf.Clamp01((Time.unscaledTime - _start) / _dur);
            float e = 1f - (1f - u) * (1f - u) * (1f - u);   // ease-out cubic
            var tr = transform;
            tr.localPosition = Vector3.LerpUnclamped(_fromPos, _toPos, e);
            tr.localRotation = Quaternion.SlerpUnclamped(_fromRot, _toRot, e);
            tr.localScale = Vector3.LerpUnclamped(_fromScale, _toScale, e);
            if (u >= 1f) _animating = false;
        }

        private static bool Approx(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-8f;
    }
}
