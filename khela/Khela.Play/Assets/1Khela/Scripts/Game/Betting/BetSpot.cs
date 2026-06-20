using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// The drop target on the table — drag a chip here to add it to the bet. Needs a Collider (on the drop
    /// layer). It parks each dropped chip on a small stack so the wager is visible, and clears the stack when
    /// the bet resets (Clear / Deal). Assign the same <see cref="BetBuilder"/> the drag controller uses.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class BetSpot : MonoBehaviour
    {
        [SerializeField] private BetBuilder builder;
        [Tooltip("Where dropped chips stack. Defaults to this transform.")]
        [SerializeField] private Transform stackAnchor;
        [Tooltip("Vertical gap (world units) between stacked chips.")]
        [SerializeField] private float stackStep = 0.03f;

        private readonly List<GameObject> _stack = new List<GameObject>();

        private void OnEnable()  { if (builder != null) builder.OnBetChanged += OnBet; }
        private void OnDisable() { if (builder != null) builder.OnBetChanged -= OnBet; }

        // Bet went to zero (Clear or Deal) → remove the visible chip stack.
        private void OnBet(decimal total) { if (total == 0m) Clear(); }

        /// <summary>Park a dropped chip on top of the stack (called by <see cref="ChipDragController"/>).</summary>
        public void Stack(GameObject chip)
        {
            var anchor = stackAnchor != null ? stackAnchor : transform;
            chip.transform.SetParent(anchor, worldPositionStays: false);
            chip.transform.localPosition = new Vector3(0f, stackStep * _stack.Count, 0f);
            chip.transform.localRotation = Quaternion.identity;
            _stack.Add(chip);
        }

        public void Clear()
        {
            for (int i = 0; i < _stack.Count; i++)
                if (_stack[i] != null) Destroy(_stack[i]);
            _stack.Clear();
        }
    }
}
