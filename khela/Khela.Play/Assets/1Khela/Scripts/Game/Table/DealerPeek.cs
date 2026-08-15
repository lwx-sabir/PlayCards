using System.Collections;
using PlayCard.Game.Betting;
using PlayCard.Game.Cards;
using PlayCard.Game.Dtos;
using UnityEngine;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Plays the dealer's PEEK gesture ONCE per round — she checks her hole card when her up-card is a 10-value
    /// (10/J/Q/K) or an Ace. Pure presentation: the server already resolved the rule (StartPlayOrPeek), so this only
    /// shows her checking. Drives <see cref="DealerAnimator.PlayPeek"/>, whose clip event tilts the hole card.
    ///
    /// TWO entry points, because a dealer BLACKJACK settles the round INSTANTLY:
    ///  • mid-round (this component) — the common case: deal lands, no dealer blackjack, she peeks, then you act. The
    ///    peek HOLDS the decision gate so you can't act until she's done.
    ///  • round-end (<see cref="PeekIfMissed"/>, called by the RoundEndDirector) — on a dealer natural the round is over
    ///    before this component gets a window, so the director plays it just before the reveal: she checks, THEN turns
    ///    it over. No decision gate there — the round is already decided.
    /// Put it on a persistent table object; refs auto-find.
    /// </summary>
    public sealed class DealerPeek : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [SerializeField] private DealerAnimator dealer;
        [SerializeField] private BlackjackTableView view;

        [Tooltip("Longest the peek will wait for the opening deal to finish landing before playing anyway. Must " +
                 "comfortably exceed a full multi-seat deal, or she peeks while someone is still being dealt to — " +
                 "a live turn releases the hold on its own, so this only needs to be a backstop.")]
        [SerializeField] private float settleWaitSeconds = 6f;

        [Tooltip("Chip stacks she takes the LOST insurance bets from. Auto-found.")]
        [SerializeField] private BetStacks betStacks;

        [Tooltip("Seconds for a taken insurance stake to fly to the dealer. Costs the player nothing: the whole " +
                 "settle runs inside the peek's decision hold, so the turn clock has not started yet.")]
        [SerializeField] private float insuranceFlightSeconds = 0.45f;

        private bool _peeked;                 // once per round — survives the settle so the director can see it
        private bool _prevInRound;
        private bool _running;
        private const int AceFaceVal = 14;    // FaceValue.Ace on the wire; 10-value cards are 10..13 (10/J/Q/K)

        /// <summary>True once she has peeked this round (re-armed on the next deal, NOT at settle — so the round-end
        /// director can tell whether the peek still owes a showing).</summary>
        public bool PeekedThisRound => _peeked;

        private void OnEnable()
        {
            if (dealer == null) dealer = FindAnyObjectByType<DealerAnimator>();
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();
            if (betStacks == null) betStacks = FindAnyObjectByType<BetStacks>(FindObjectsInactive.Include);
            if (table != null) table.OnBoardChanged += OnBoard;
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
            // Torn down mid-peek → release the decision gate, or the player could never act again.
            if (_running && view != null) view.EndPeek();
            _running = false;
        }

        private void OnBoard(BoardSnapshot board) => TryPeek(board);

        // DecisionReady flips between board pushes (the deal lands mid-animation), so also poll — the guards make it cheap.
        private void Update() => TryPeek(table != null ? table.Board : null);

        /// <summary>
        /// WHEN THE DEALER PEEKS — the whole rule, in two lines. Everything below implements exactly this, and any
        /// change here should be checked against it rather than against the mechanism that happens to enforce it:
        ///
        ///   TEN up-card  → peek once EVERY player's second card is down, the dealer's included.
        ///   ACE up-card  → peek once EVERY player has finished their insurance decision.
        ///
        /// She peeks after the deal, never during it, and never before players have decided something she is about
        /// to resolve. This has been broken twice by conditions that were true "most of the time" — a card that has
        /// not been dealt cannot be looked at, so the guards test for the card itself, not for the absence of
        /// movement.
        /// </summary>
        private void TryPeek(BoardSnapshot board)
        {
            if (board == null) return;

            // Re-arm on the START of a NEW round, NOT at settle — a dealer blackjack ends the round instantly, and the
            // director still needs to see that the peek never ran so it can show it before the reveal.
            bool inRound = board.RoundInProgress;
            if (inRound && !_prevInRound) _peeked = false;
            _prevInRound = inRound;

            if (!inRound) return;                              // round over → the director handles any missed peek
            if (_peeked || _running || dealer == null) return;

            // INSURANCE FIRST. With an Ace up-card the server opens the INSURANCE window and only peeks once it CLOSES
            // (BeginPlayOrInsurance → … → CloseInsurancePhase → StartPlayOrPeek). Hold the animation until the window is
            // gone, or she'd be checking her hole card while players are still deciding — the wrong order.
            if (board.InsuranceExpiresAt.HasValue) return;

            if (!UpCardWarrantsPeek(board)) return;

            // RESERVE the decision gate the INSTANT a peek is owed — BEFORE the opening deal finishes landing. Otherwise
            // the deal lands (DecisionReady flips true) a frame or two before BeginPeek runs, and in that gap the
            // /presented handshake collapses the turn clock early — so the peek animation eats into the player's decision
            // time and the clock is already running before the decision HUD appears. RunHeld waits out the deal, then peeks.
            _peeked = true;
            StartCoroutine(RunHeld());
        }

        // Mid-round peek: holds the decision gate from the moment it's owed until she's finished, so the turn clock and
        // the decision HUD (both gated on DecisionReady) stay parked through the deal AND the peek — no eaten time.
        private IEnumerator RunHeld()
        {
            _running = true;
            if (view != null) view.BeginPeek();   // take the gate up front, before the deal has settled

            // Wait for the ENTIRE opening deal to land before she checks her hole card — that is the real-table
            // order, and it is what a player expects to see.
            //
            // AnyCardAnimating covers every seat; the old check was SeatSettled(0), the DEALER's seat alone. She is
            // dealt last, so her cards landing was assumed to mean the deal was over — but the seats are dealt in
            // parallel with a stagger, so hers can settle while a later player is still waiting on their second
            // card. That is precisely the "she peeked while seat 2 had one card" case: the condition was answering
            // a narrower question than the one being asked.
            if (view != null)
            {
                float t = 0f;
                // Two conditions, and the second is the one that actually guarantees correctness: she must HAVE a
                // hole card, on the felt, before she can look at it. AnyCardAnimating alone is not enough — this
                // method also runs every frame from Update(), so any momentary gap where nothing happens to be in
                // flight let the peek through mid-deal, before her second card had been thrown.
                while (t < settleWaitSeconds && (view.AnyCardAnimating() || !HoleCardIsDown()))
                {
                    // The round SETTLED while we were still waiting for the deal to land — a natural blackjack does
                    // exactly this, settling a tick after the deal. The RoundEndDirector now owns the dealer rig, so
                    // playing the peek here would fight its reveal/collect/pay clips. Abandon this attempt and hand
                    // the peek back: clearing _peeked lets the director's PeekIfMissed play it at the proper beat,
                    // just before the reveal.
                    if (table != null && table.Board != null && !table.Board.RoundInProgress)
                    {
                        if (view != null) view.EndPeek();
                        _peeked = false;
                        _running = false;
                        yield break;
                    }
                    t += Time.deltaTime;
                    yield return null;
                }
            }

            if (!HoleCardIsDown())
            {
                // Timed out still without a hole card to look at. Playing the clip now would lift a card that is
                // not there and, until CardFlip started guarding it, threw out of the animation event. Hand the
                // peek back so the round-end director can play it at the reveal instead.
                Debug.LogWarning("[DealerPeek] skipped: no hole card on the felt after " +
                                 $"{settleWaitSeconds:0.#}s (dealer cards: {DealerCardCount()}).");
                if (view != null) view.EndPeek();
                _peeked = false;
                _running = false;
                yield break;
            }

            yield return dealer.PlayPeek();

            // Insurance is decided by what she just looked at, so it settles HERE — before the gate is released and
            // therefore before the turn clock starts. That ordering is the whole reason this lives inside the hold:
            // /presented is withheld while the gate is up, so the server's deadline stays on its generous ceiling and
            // the dealer taking the lost insurance costs the first player none of their decision time.
            //
            // Only the LOSS is settled here. A dealer natural ends the round on the spot, so a winning insurance bet
            // is paid by the RoundEndDirector as part of the ceremony, beside the stack it was placed on.
            yield return TakeLostInsurance();

            if (view != null) view.EndPeek();
            _running = false;
        }

        /// <summary>
        /// She checked, she does not have blackjack: every insurance bet on the table is lost, and she takes them one
        /// seat at a time with the same gesture she uses for any other collect.
        ///
        /// Real-table order, and it matters for readability: the insurance question is asked and answered before play
        /// begins. Leaving the losing stakes on the felt until the round ends would leave a dead wager sitting beside
        /// every live bet for the whole hand, and settle it long after the moment that decided it.
        /// </summary>
        private IEnumerator TakeLostInsurance()
        {
            var board = table != null ? table.Board : null;
            if (board?.Seats == null || betStacks == null) yield break;
            if (DealerHasNatural(board)) yield break;        // insurance WON — the director pays it at settle

            foreach (var seat in board.Seats)
            {
                var hands = seat?.Player?.Hands;
                if (hands == null || hands.Count == 0) continue;
                if (hands[0].Insurance <= 0m) continue;
                if (betStacks.InsuranceSettled(seat.SeatNumber)) continue;   // already taken (a repeated push)

                int s = seat.SeatNumber;
                if (dealer != null) yield return dealer.CollectFromSeat(s, () => TakeInsuranceChips(s));
                else TakeInsuranceChips(s);
                yield return new WaitForSecondsRealtime(insuranceFlightSeconds);
            }
            if (dealer != null) dealer.ReturnToIdle();
        }

        /// <summary>Fly one seat's insurance chips to the dealer and stop the board rebuilding them.</summary>
        private void TakeInsuranceChips(int seatNumber)
        {
            var chips = betStacks.DetachInsurance(seatNumber);
            if (chips == null || chips.Count == 0) return;

            var hub = (dealer != null && dealer.ChipHandPoint != null) ? dealer.ChipHandPoint
                    : (view != null ? view.DealSource : null);
            if (hub == null)
            {
                for (int i = 0; i < chips.Count; i++) if (chips[i] != null) Destroy(chips[i]);
                return;
            }

            for (int i = 0; i < chips.Count; i++)
            {
                var chip = chips[i];
                if (chip == null) continue;
                var tr = chip.transform;
                var mover = chip.GetComponent<CardMover>() ?? chip.AddComponent<CardMover>();
                Vector3 local = tr.parent != null ? tr.parent.InverseTransformPoint(hub.position) : hub.position;
                mover.Target(local, tr.localRotation, tr.localScale, insuranceFlightSeconds);
                StartCoroutine(DestroyAfter(chip, insuranceFlightSeconds + 0.05f));
            }
        }

        private IEnumerator DestroyAfter(GameObject go, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (go != null) Destroy(go);
        }

        /// <summary>Her hole card has been dealt and is actually sitting on the felt — not still parked at the shoe.</summary>
        private bool HoleCardIsDown()
            => DealerCardCount() >= 2 && (view == null || view.SeatSettled(0));

        private int DealerCardCount()
        {
            var cards = table != null && table.Board != null && table.Board.Dealer != null
                ? table.Board.Dealer.Cards : null;
            return cards != null ? cards.Count : 0;
        }

        // NOTE: an earlier attempt released the hold as soon as the board said the turn was mine. That is wrong at
        // the OPENING deal: with a ten up-card there is no insurance window, so the server hands out the turn in the
        // same board that reveals the up-card — the release fired on the first frame and she peeked before a single
        // card had been thrown. The post-insurance hang it was meant to fix is already handled by waiting on
        // AnyCardAnimating(), which is false by then because the deal landed long ago.

        /// <summary>
        /// ROUND-END entry point. On a dealer BLACKJACK the server settles instantly, so the mid-round trigger never got
        /// a window — the RoundEndDirector calls this just before the reveal so she's SEEN to check before turning the
        /// card over. No-op if she already peeked this round or her up-card never warranted one. Takes no decision gate:
        /// the round is already decided.
        /// </summary>
        public IEnumerator PeekIfMissed(BoardSnapshot board)
        {
            if (_peeked || _running || dealer == null || board == null) yield break;
            if (!UpCardWarrantsPeek(board)) yield break;

            // She only checks if there is something to find. A peek is a question — "do I already have blackjack?" —
            // and by the time this runs the round is over and every hand is resolved, so the only answer worth showing
            // is yes. With no natural under there she would be miming a check whose result changes nothing, AFTER the
            // players have finished playing, which reads as the table stalling rather than as suspense.
            //
            // This is the fallback path, and it exists solely for the natural: a dealer blackjack settles the round the
            // instant it is dealt, so the mid-round trigger never gets its window and she would otherwise flip the card
            // without ever being seen to look. Everything else is a peek the player has no reason to watch — most often
            // one the mid-round trigger deliberately handed back after abandoning mid-deal (see RunHeld).
            if (!DealerHasNatural(board)) yield break;

            _peeked = true;
            _running = true;
            yield return dealer.PlayPeek();
            _running = false;
        }

        /// <summary>
        /// Her two cards are an Ace + a ten-value — a natural. Safe to read at round-end: the settle snapshot carries
        /// the real hole card (the view masks it visually until the reveal beat, the DATA is already true).
        /// </summary>
        private static bool DealerHasNatural(BoardSnapshot board)
        {
            var cards = board.Dealer?.Cards;
            if (cards == null || cards.Count != 2) return false;
            var a = cards[0];
            var b = cards[1];
            if (a == null || b == null) return false;

            bool aceA = a.FaceVal == AceFaceVal, aceB = b.FaceVal == AceFaceVal;
            bool tenA = a.FaceVal >= 10 && a.FaceVal < AceFaceVal;   // 10/J/Q/K
            bool tenB = b.FaceVal >= 10 && b.FaceVal < AceFaceVal;
            return (aceA && tenB) || (aceB && tenA);
        }

        // Real blackjack peeks only on a 10-value or Ace up-card.
        private static bool UpCardWarrantsPeek(BoardSnapshot board)
        {
            var cards = board.Dealer?.Cards;
            if (cards == null || cards.Count < 2) return false;
            var up = cards[0];
            return up != null && up.FaceVal >= 10 && up.FaceVal <= AceFaceVal;   // 10/J/Q/K (10..13) or Ace (14)
        }
    }
}
