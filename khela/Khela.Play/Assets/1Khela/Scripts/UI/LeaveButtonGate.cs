using PlayCard.Game.Table;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Locks the Home / Back (leave) button(s) while the LOCAL player is IN the current round, so they can't bail on a
    /// live hand they've staked. Unlocks the moment the round settles (server-authoritative), and never locks a
    /// spectator / between-rounds player — they can leave any time.
    ///
    /// Assign the leave <see cref="buttons"/> (their <c>interactable</c> is toggled). Optionally assign a
    /// <see cref="lockIndicator"/> (e.g. a small lock icon) shown only while locked. Put this controller on an
    /// ALWAYS-ACTIVE object.
    /// </summary>
    public sealed class LeaveButtonGate : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("Home / Back / Leave buttons to disable while the local player is in a live round.")]
        [SerializeField] private Selectable[] buttons;
        [Tooltip("Optional visual (e.g. a lock icon) shown only while leaving is blocked.")]
        [SerializeField] private GameObject lockIndicator;

        private bool _lockedApplied;
        private bool _first = true;

        private void Awake()
        {
            if (table == null) table = FindAnyObjectByType<TableController>();
        }

        private void Update()
        {
            if (table == null) return;
            bool locked = table.AmIInRound;
            if (!_first && locked == _lockedApplied) return;
            _first = false;
            _lockedApplied = locked;

            if (buttons != null)
                foreach (var b in buttons)
                    if (b != null) b.interactable = !locked;

            if (lockIndicator != null && lockIndicator.activeSelf != locked)
                lockIndicator.SetActive(locked);
        }
    }
}
