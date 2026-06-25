using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Moves a single glow under the hand currently being played (the board's <c>CurrentSeatNumber</c> +
    /// <c>CurrentHandIndex</c>), so everyone can see whose turn it is — split-aware, because it positions via
    /// <see cref="BlackjackTableView.HandCenterLocal"/> (the same centred-split math the cards use). The glow's
    /// WIDTH grows with the number of cards in the active hand so it spans the whole fan. Hidden between rounds and
    /// while the dealer plays. Only one hand is ever active, so one glow is repositioned rather than a grid of
    /// per-seat objects. The glow's own <see cref="UiPulse"/> makes it breathe (keep its Scale Pulse = 0 so it
    /// doesn't fight the width scaling — use the alpha pulse). Put this on an always-active object (e.g. the
    /// InfoCanvas). Right-click ▸ "Test: show glow at seat 1" to verify the glow renders, independent of the board.
    /// </summary>
    public sealed class ActiveHandHighlighter : MonoBehaviour
    {
        private enum Axis { X, Y, Z }

        [SerializeField] private TableController table;
        [Tooltip("The table view — used for the seat anchors + the centred split offset, so the glow lands on the right hand.")]
        [SerializeField] private BlackjackTableView view;
        [Tooltip("The single glow shown under the active hand. Give it a UiPulse to breathe (Scale Pulse = 0); a world-space ring/underlay.")]
        [SerializeField] private GameObject glow;
        [Tooltip("Extra local rotation to lay the glow FLAT under the cards. A world-space UI ring stands vertical by " +
                 "default, so try (90,0,0); tune if it stands up or faces away. Applied in the seat anchor's frame, so " +
                 "ONE value works for all 3 seats regardless of how each anchor is rotated.")]
        [SerializeField] private Vector3 glowLocalEuler = new Vector3(90f, 0f, 0f);
        [Tooltip("Lift along the felt normal so the glow sits ABOVE the felt (not buried in it / occluded). Keep small.")]
        [SerializeField] private float glowLift = 0.01f;

        [Header("Width-to-fit")]
        [Tooltip("How many cards the glow's AUTHORED width covers (you sized it for 2). The glow's width scales by " +
                 "cardsInHand / this, so it grows to span the whole fan. Its pivot must be CENTRED so it grows both ways.")]
        [Min(1)] [SerializeField] private int referenceCardCount = 2;
        [Tooltip("Which of the glow's LOCAL axes is its width (the direction along the fan). Default X; switch if it " +
                 "grows the wrong way (depends on how the glow art + Glow Local Euler are set up).")]
        [SerializeField] private Axis widthAxis = Axis.X;

        private bool _shown;
        private bool _warned;
        private Vector3 _baseGlowScale = Vector3.one;
        private bool _capturedBase;

        private void OnEnable()
        {
            CaptureBaseScale();
            if (table == null) { WarnUnwired(); return; }
            table.OnBoardChanged += OnBoard;
            OnBoard(table.Board);   // board may have arrived before we enabled (Board null is handled)
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (glow == null || view == null) { WarnUnwired(); return; }

            int seat = board != null ? board.CurrentSeatNumber : -1;   // 1-based active seat; -1 between hands / dealer
            bool active = board != null && board.RoundInProgress && seat > 0;
            var anchor = active ? view.SeatAnchor(seat) : null;
            if (anchor == null) { Show(false); return; }

            var hands = board.Seats?.Find(x => x.SeatNumber == seat)?.Player?.Hands;
            int handCount = (hands != null && hands.Count > 0) ? hands.Count : 1;
            int handIndex = Mathf.Clamp(board.CurrentHandIndex, 0, handCount - 1);
            int cardCount = (hands != null && handIndex < hands.Count) ? (hands[handIndex].Cards?.Count ?? 0) : 0;

            Place(anchor, handIndex, handCount, cardCount);
            Show(true);
        }

        // Position in WORLD space (no reparent — keeps the glow under its own world canvas) at the active hand's
        // centre, lifted off the felt + laid flat, and widened to span the fan.
        private void Place(Transform anchor, int handIndex, int handCount, int cardCount)
        {
            CaptureBaseScale();

            glow.transform.SetPositionAndRotation(
                anchor.TransformPoint(view.HandCenterLocal(handIndex, handCount) + Vector3.up * glowLift),
                anchor.rotation * Quaternion.Euler(glowLocalEuler));

            // Grow the WIDTH to cover the whole fan: scale the width axis by cards-in-hand / the authored count.
            float factor = Mathf.Max(1, cardCount) / (float)Mathf.Max(1, referenceCardCount);
            var ls = _baseGlowScale;
            switch (widthAxis)
            {
                case Axis.X: ls.x *= factor; break;
                case Axis.Y: ls.y *= factor; break;
                case Axis.Z: ls.z *= factor; break;
            }
            glow.transform.localScale = ls;
        }

        private void Show(bool on)
        {
            if (glow.activeSelf != on) glow.SetActive(on);
            if (_shown != on) { _shown = on; Debug.Log($"[ActiveHand] glow {(on ? "ON" : "off")}"); }   // state-change only
        }

        // Capture the glow's authored scale ONCE (before we ever scale it) so width-to-fit is relative to it.
        private void CaptureBaseScale()
        {
            if (_capturedBase || glow == null) return;
            _baseGlowScale = glow.transform.localScale;
            _capturedBase = true;
        }

        private void WarnUnwired()
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning($"[ActiveHand] not wired — assign glow + view (and table). " +
                             $"glow={(glow != null)}, view={(view != null)}, table={(table != null)}", this);
        }

        // Isolate rendering from logic: force the glow on at seat 1, hand 0. If you STILL see nothing, the glow
        // itself isn't rendering (world-space canvas / material / scale / behind geometry) — not the board logic.
        [ContextMenu("Test: show glow at seat 1")]
        private void TestShow()
        {
            if (glow == null || view == null) { WarnUnwired(); return; }
            var anchor = view.SeatAnchor(1);
            if (anchor == null) { Debug.LogWarning("[ActiveHand] seat-1 anchor not set on the view.", this); return; }
            Place(anchor, 0, 1, referenceCardCount);
            glow.SetActive(true);
            Debug.Log("[ActiveHand] TEST show at seat 1 — if nothing appears, it's the glow's rendering, not the logic.", this);
        }
    }
}
