using System.Collections;
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

            // Let the opening deal finish landing first, so the peek visibly FOLLOWS the deal. The dealer is dealt LAST,
            // so her cards settling means the whole deal is down. Raw settled check (not DecisionReady — we've already
            // taken the peek hold, which forces it false here); an 8s timeout guards a deal that never reports settled
            // (e.g. a mid-round resync), and stays well under the server's turn ceiling so it can't cause an auto-stand.
            if (view != null)
            {
                float t = 0f;
                while (t < 8f && !view.SeatSettled(0))
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

            yield return dealer.PlayPeek();
            if (view != null) view.EndPeek();
            _running = false;
        }

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

            _peeked = true;
            _running = true;
            yield return dealer.PlayPeek();
            _running = false;
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
