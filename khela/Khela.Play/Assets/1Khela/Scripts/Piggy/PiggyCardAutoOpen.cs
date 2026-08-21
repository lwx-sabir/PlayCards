using Khela.Common.Piggy;
using PlayCard.UI;
using UnityEngine;

namespace PlayCard.Piggy
{
    /// <summary>
    /// Wakes the folded piggy card for its celebration moment.
    ///
    /// The widget that throws the chips lives INSIDE the card, and a folded card is an inactive one — the widget
    /// can neither watch the state nor celebrate it, and nothing else on Home refreshes piggy state at all. This
    /// sits on an always-active object next to the card's foldout, keeps the state warm while the card sleeps,
    /// and the moment the server reports a celebration-worthy unseen amount it opens the card exactly as a tap
    /// would. The woken widget does the rest itself: refreshes, sees the delta, and throws the chips — a beat
    /// later (its celebrate delay), so the card has finished unfolding before pieces fly at the pig.
    ///
    /// The card is never auto-closed. It stays until the player dismisses it, like any card they opened by hand.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PiggyCardAutoOpen : MonoBehaviour
    {
        [Tooltip("The piggy card's foldout. Empty = the one on this object.")]
        [SerializeField] private HudCardFoldout foldout;

        [Tooltip("Seconds between state refreshes while the card is closed — the sleeping card refreshes nothing " +
                 "itself, so this is piggy state's only heartbeat on Home. Coarse on purpose: it only decides how " +
                 "soon after a session the pig calls out. PiggyState's own staleness gate keeps it cheap.")]
        [SerializeField] private float refreshEvery = 20f;

        // The delta already opened for. An auto-open the player dismissed must not repeat for the SAME chips —
        // the server keeps reporting the delta until the celebration ack lands, and every report would re-open
        // the card in the player's face. Cleared when the server's number shrinks (the ack arrived).
        private decimal _openedFor;
        private float _nextRefresh;

        private void Awake()
        {
            if (foldout == null) foldout = GetComponent<HudCardFoldout>();
        }

        private void OnEnable()
        {
            PiggyState.Instance.Changed += OnChanged;
            OnChanged(PiggyState.Instance.Current);
            _ = PiggyState.Instance.RefreshAsync();
            _nextRefresh = Time.unscaledTime + Mathf.Max(5f, refreshEvery);
        }

        private void OnDisable()
        {
            PiggyState.Instance.Changed -= OnChanged;
        }

        private void Update()
        {
            if (foldout == null || foldout.IsOpen || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + Mathf.Max(5f, refreshEvery);
            _ = PiggyState.Instance.RefreshAsync();
        }

        private void OnChanged(PiggyStateDto state)
        {
            if (state == null || !state.Enabled || foldout == null) return;

            // The server's number shrank: the last celebration's ack landed. The next worthy amount may open again.
            if (state.UnseenAccrued < _openedFor) _openedFor = 0m;

            if (foldout.IsOpen) return;
            if (state.UnseenAccrued <= 0m || state.UnseenAccrued < state.MinFlyAmount) return;
            if (state.UnseenAccrued == _openedFor) return;   // same chips; the player already closed the card on them

            _openedFor = state.UnseenAccrued;
            foldout.Open();
        }
    }
}
