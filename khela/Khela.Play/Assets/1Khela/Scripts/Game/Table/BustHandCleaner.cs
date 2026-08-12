using System.Collections;
using System.Collections.Generic;
using PlayCard.Game.Betting;
using PlayCard.Game.Cards;
using PlayCard.Game.Dtos;
using UnityEngine;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Settles a BUSTED hand the moment it busts, instead of leaving it on the felt until the round ends.
    ///
    /// A bust is already decided — nothing that happens later can change it — so the dealer takes that hand's chips
    /// and clears its cards straight away, exactly as she would at a real table. Waiting until the round-end ceremony
    /// meant a busted hand sat there wearing its BUST badge through every other player's turn, which at a full table
    /// is a long time to look at a hand that is over.
    ///
    /// Purely presentational: the money moved server-side at the deal (debit-on-bet), and the server still reports the
    /// hand at settle. This only decides WHEN the felt stops showing it. The round-end director skips anything already
    /// taken, so nothing is collected twice.
    ///
    /// Put it on an always-active object in the Table scene; every reference auto-finds.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BustHandCleaner : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private TableController table;
        [SerializeField] private BlackjackTableView view;
        [SerializeField] private BetStacks betStacks;
        [SerializeField] private DealerAnimator dealer;

        [Header("Timing")]
        [Tooltip("Pause after the busting card LANDS before the dealer takes the hand — the beat where the player " +
                 "reads the card that broke them and the BUST badge. Measured from the card touching the felt, so " +
                 "this is the whole time the badge is up.")]
        [SerializeField] private float readSeconds = 1.4f;

        [Tooltip("Seconds for the taken chips to fly to the dealer.")]
        [SerializeField] private float chipFlightSeconds = 0.45f;

        [Tooltip("Gap between the chips leaving and the cards being swept, so the two read as one sequence rather " +
                 "than happening at once.")]
        [SerializeField] private float cardSweepDelay = 0.15f;

        [Tooltip("Play the dealer's COLLECT gesture for the seat. Skipped automatically while she is mid-deal — she " +
                 "cannot throw and scoop at the same time, and the deal must never be interrupted.")]
        [SerializeField] private bool playDealerGesture = true;

        // Hands already handled this round, so a repeated board push can't take the same hand twice.
        private readonly HashSet<int> _handled = new HashSet<int>();
        private bool _prevInRound;

        private static int Key(int seat, int hand) => seat * 100 + hand;

        private void OnEnable()
        {
            if (table == null) table = FindAnyObjectByType<TableController>(FindObjectsInactive.Include);
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>(FindObjectsInactive.Include);
            if (betStacks == null) betStacks = FindAnyObjectByType<BetStacks>(FindObjectsInactive.Include);
            if (dealer == null) dealer = FindAnyObjectByType<DealerAnimator>(FindObjectsInactive.Include);

            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (board == null) return;

            bool inRound = board.RoundInProgress;
            if (inRound && !_prevInRound) _handled.Clear();   // new round — everything is live again
            _prevInRound = inRound;
            if (!inRound || board.Seats == null) return;

            foreach (var seat in board.Seats)
            {
                var hands = seat?.Player?.Hands;
                if (hands == null) continue;

                for (int h = 0; h < hands.Count; h++)
                {
                    var hand = hands[h];
                    if (hand == null || hand.HandValue <= 21) continue;   // 21 or under is still in play

                    int key = Key(seat.SeatNumber, h);
                    if (!_handled.Add(key)) continue;                     // already taking it
                    StartCoroutine(TakeBustedHand(seat.SeatNumber, h, hand.Cards != null ? hand.Cards.Count : 0));
                }
            }
        }

        private IEnumerator TakeBustedHand(int seat, int handIndex, int cardCount)
        {
            // Wait for the card that BROKE the hand to actually land — the bust is announced by the board a beat
            // before the card arrives, and taking the chips over a card still in flight reads as the dealer punishing
            // you before you have seen why.
            float guard = Time.unscaledTime + 5f;   // never wait forever on a card that goes missing
            while (cardCount > 0 && !view.CardSettled(seat, handIndex, cardCount - 1) && Time.unscaledTime < guard)
                yield return null;

            // This hand is on its way out — tell the view now, so it is never TUCKED. Without this a busted split hand
            // shrinks away at the tuck delay and is then collected and swept a beat later: two conflicting gestures
            // for one hand. Marking it early also un-tucks it if it already had.
            view.MarkHandClearing(seat, handIndex);

            if (readSeconds > 0f) yield return new WaitForSecondsRealtime(readSeconds);
            if (view.IsHandCleared(seat, handIndex)) yield break;   // the round ended underneath us

            // The dealer's scoop, but ONLY if she is free. She cannot throw a card and collect at the same time, and
            // the deal pump owns her while anything is still being dealt — interrupting it would strand a card.
            bool gesture = playDealerGesture && dealer != null && !view.AnyCardAnimating();
            bool flew;
            if (gesture)
            {
                bool grabbed = false;
                yield return dealer.CollectFromSeat(seat, () => grabbed = TakeChips(seat, handIndex));
                flew = grabbed;
            }
            else flew = TakeChips(seat, handIndex);

            // The chips must ARRIVE before the cards move. CollectFromSeat returns at the clip's release frame — the
            // moment she lets go — not when the chips have finished flying, so without this wait the sweep started
            // while the wager was still in the air and the two gestures overlapped.
            if (flew) yield return new WaitForSecondsRealtime(chipFlightSeconds);
            if (cardSweepDelay > 0f) yield return new WaitForSecondsRealtime(cardSweepDelay);

            // Cards last: the hand's chips are gone, now the hand itself goes. This also removes its BUST badge, which
            // is pinned to the cards.
            if (view != null) view.ClearHand(seat, handIndex);
            if (gesture && dealer != null) dealer.ReturnToIdle();
        }

        /// <summary>Fly this hand's committed chips to the dealer and stop the board rebuilding them.</summary>
        /// <returns>True if chips are actually in flight, so the caller knows to wait for them to land.</returns>
        private bool TakeChips(int seat, int handIndex)
        {
            if (betStacks == null) return false;

            var chips = betStacks.ClearHandMidRound(seat, handIndex);
            if (chips == null || chips.Count == 0) return false;

            var hub = (dealer != null && dealer.ChipHandPoint != null) ? dealer.ChipHandPoint
                    : (view != null ? view.DealSource : null);
            if (hub == null)
            {
                for (int i = 0; i < chips.Count; i++) if (chips[i] != null) Destroy(chips[i]);
                return false;
            }

            for (int i = 0; i < chips.Count; i++)
            {
                var chip = chips[i];
                if (chip == null) continue;
                var tr = chip.transform;
                var mover = chip.GetComponent<CardMover>() ?? chip.AddComponent<CardMover>();
                Vector3 local = tr.parent != null ? tr.parent.InverseTransformPoint(hub.position) : hub.position;
                mover.Target(local, tr.localRotation, tr.localScale, chipFlightSeconds);
                StartCoroutine(DestroyAfter(chip, chipFlightSeconds + 0.05f));
            }
            return true;
        }

        private IEnumerator DestroyAfter(GameObject go, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (go != null) Destroy(go);
        }
    }
}
