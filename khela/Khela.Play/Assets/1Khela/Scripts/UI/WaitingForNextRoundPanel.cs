using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Shows a small "waiting for next round" panel while the LOCAL player is SEATED but not in the current round —
    /// i.e. they joined mid-match (or sat a round out) and are watching it live until the next deal. Hidden the
    /// instant a round they're in starts, and between rounds (when they can bet).
    ///
    /// Pure show/hide: assign <see cref="panel"/> = the panel GameObject (skin/animate it however you like). Put this
    /// controller on an ALWAYS-ACTIVE object so its Update runs even while the panel is hidden.
    /// </summary>
    public sealed class WaitingForNextRoundPanel : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("The panel to show while spectating a round in progress. May be disabled by default.")]
        [SerializeField] private GameObject panel;

        private void Awake()
        {
            if (table == null) table = FindAnyObjectByType<TableController>();
        }

#if UNITY_EDITOR
        // Guard the recurring "disabled-watcher" trap: a controller placed ON the panel it toggles never runs its
        // Update while that panel is hidden, so it can never show itself. Catch it loudly on scene load / edit.
        private void OnValidate()
        {
            if (panel == gameObject)
                Debug.LogError($"[{nameof(WaitingForNextRoundPanel)}] is on the SAME GameObject as its 'panel', which " +
                    "is off by default — its Update() will never run, so the panel can never appear. Move this component " +
                    "to an ALWAYS-ACTIVE object (e.g. TableHUD) and set 'panel' to the hidden panel GameObject.", this);
        }
#endif

        // Poll each frame: the state derives from the live board plus the round-end ceremony (which ends between
        // pushes), and the check is only a couple of bools. Keeps this independent of event-subscription lifetime.
        private void Update() => Apply();

        private void Apply()
        {
            if (panel == null || table == null) return;
            bool show = table.AmISpectatingRound;
            if (panel.activeSelf != show) panel.SetActive(show);
        }
    }
}
