using System.Collections;
using System.Collections.Generic;
using Animancer;
using PlayCard.Game.Betting;
using PlayCard.Game.Cards;
using PlayCard.Game.Dtos;
using PlayCard.UI;   // SeatPlates + WinChipFly — settle-reactive balance juice the director gates (single assembly, no asmdef split)
using UnityEngine;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// SOLE owner of the blackjack round-end presentation. On the atomic settle push (RoundInProgress true → false) it
    /// TAKES OVER the felt and plays one blocking sequence:
    ///   REVEAL → DRAWS → HOLD → COLLECT → PAY → SWEEP → DONE.
    /// The view, dealer conductor, chip stacks and camera do NOT independently react to the settle push while this
    /// holds — they honour a hold and expose commands this director calls. The whole-table gate
    /// (<see cref="BlackjackTableView.RoundEndSettling"/>) stays latched for the entire sequence.
    ///
    /// Wiring: put this on a scene object; assign the refs + the dealer hand anchor. The REVEAL is one clip on this
    /// component, its event bound to <see cref="FlipHole"/>. COLLECT and PAY are PER-SEAT and one-at-a-time, exactly like
    /// the deal throws: author them on the <see cref="DealerAnimator"/> (seatCollects / seatPays), each clip's event bound
    /// to <c>DealerAnimator.ReleaseChip</c> — the director drives one loser/winner at a time and each clip's event flies
    /// THAT seat's chips. A missing clip or event degrades gracefully (the chips fly immediately); nothing ever stalls
    /// (watchdog + <see cref="OnDisable"/> force-finish).
    ///
    /// Money-safety: every chip visual is driven by <see cref="BoardSnapshot.LastResults"/> (Outcome / Delta) — the
    /// server is authoritative on balances; the chips never imply a payout the server didn't make.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoundEndDirector : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private TableController table;
        [SerializeField] private BlackjackTableView view;
        [SerializeField] private DealerAnimator dealer;
        [SerializeField] private BetStacks betStacks;
        [Tooltip("Seat banner cards. The credited balance on them is held until the PAY beat (else the chip count jumps at settle).")]
        [SerializeField] private SeatPlates seatPlates;
        [Tooltip("Win juice (chips → balance icon). Fired on the PAY beat instead of the raw settle push. Optional.")]
        [SerializeField] private WinChipFly winChipFly;
        [Tooltip("WIN / LOSE / +delta banner. Held until the PAY beat so it doesn't announce the payout at settle. Optional.")]
        [SerializeField] private RoundResultBanner resultBanner;
        [Tooltip("Dealer peek driver. On a dealer BLACKJACK the round settles instantly, so its mid-round trigger never " +
                 "fires — this director then plays the peek just before the reveal. Optional (auto-found).")]
        [SerializeField] private DealerPeek peekDriver;
        [Tooltip("Dealer-hand point chips are GATHERED to (losers) and PAID from (winners). EMPTY = falls back to the " +
                 "view's Deal Source (her hand), so collect/pay work with your existing deal setup. Assign a dedicated " +
                 "Transform (e.g. her chip tray) to override.")]
        [SerializeField] private Transform dealerHandAnchor;

        [Header("Reveal clip — one Animancer event, bound to FlipHole (COLLECT/PAY are per-seat on the DealerAnimator)")]
        [Tooltip("Dealer reveal body clip. Its event fires FlipHole (the hole-card turn). Empty = flip fires immediately.")]
        [SerializeField] private ClipTransition reveal;

        [Tooltip("FINALE — the LAST beat, after the cards are collected. Bind RoundFinished on the frame the round should " +
                 "read as over: BETTING REOPENS THERE (the clip plays its tail out on its own). With no event it waits " +
                 "out the clip; empty = betting reopens immediately after the sweep.")]
        [SerializeField] private ClipTransition finale;

        [Header("Timing")]
        [Tooltip("Hole-card turn duration (edge-on and back). The art swaps at the apex.")]
        [SerializeField] private float flipSeconds = 0.4f;
        [Tooltip("Local-euler the hole card turns by to go edge-on. Pick the axis that turns its face away.")]
        [SerializeField] private Vector3 flipEdgeEuler = new Vector3(0f, 0f, 90f);
        [Tooltip("Pause after the final hands are shown, so they read before chips move.")]
        [SerializeField] private float holdSeconds = 0.6f;
        [Tooltip("Seconds for a chip to fly (collect or pay).")]
        [SerializeField] private float chipFlightSeconds = 0.5f;
        [Tooltip("Gap between paying one winner and the next.")]
        [SerializeField] private float payGap = 0.15f;
        [Tooltip("Max wait for cards to settle between beats.")]
        [SerializeField] private float settleTimeout = 3f;
        [Tooltip("Hard cap: force-finish the whole sequence after this, so nothing can freeze the felt.")]
        [SerializeField] private float maxHoldSeconds = 12f;

        private bool _prevInProgress;
        private bool _running;
        private BoardSnapshot _board;
        private Coroutine _seq, _watchdog, _flip;
        private float _watchdogDeadline;   // per-BEAT deadline (kicked each beat), so a slow-but-progressing sequence isn't force-finished
        private bool _flipDone, _payShown, _finaleDone;
        private readonly List<GameObject> _ownedChips = new List<GameObject>();   // director-owned flying chips

        /// <summary>True while the director owns the round-end (for optional HUD gating / tests).</summary>
        public bool Holding => _running;

        private void OnEnable()
        {
            if (table == null) table = FindAnyObjectByType<TableController>();
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();
            if (dealer == null) dealer = FindAnyObjectByType<DealerAnimator>();
            if (betStacks == null) betStacks = FindAnyObjectByType<BetStacks>();
            if (seatPlates == null) seatPlates = FindAnyObjectByType<SeatPlates>();
            if (winChipFly == null) winChipFly = FindAnyObjectByType<WinChipFly>();
            if (resultBanner == null) resultBanner = FindAnyObjectByType<RoundResultBanner>();
            if (peekDriver == null) peekDriver = FindAnyObjectByType<DealerPeek>();

            if (table != null)
            {
                table.OnBoardChanged += OnBoard;
                table.OnConnectionChanged += OnConnection;
                _prevInProgress = table.Board != null && table.Board.RoundInProgress;   // seed: no false trigger on a cold join
            }
            if (betStacks != null) betStacks.RegisterSettleDirector(this);   // arms BetStacks' INTRINSIC hold
            if (seatPlates != null) seatPlates.RegisterSettleDirector(this); // arms the seat-plate balance hold (revealed on PAY)
            if (winChipFly != null) winChipFly.RegisterSettleDirector(this); // win juice now fires on PAY, not the settle push
            if (resultBanner != null) resultBanner.RegisterSettleDirector(this); // WIN/LOSE banner held until PAY
        }

        private void OnDisable()
        {
            if (table != null) { table.OnBoardChanged -= OnBoard; table.OnConnectionChanged -= OnConnection; }
            if (betStacks != null) betStacks.UnregisterSettleDirector(this);
            if (seatPlates != null) seatPlates.UnregisterSettleDirector(this);
            if (winChipFly != null) winChipFly.UnregisterSettleDirector(this);
            if (resultBanner != null) resultBanner.UnregisterSettleDirector(this);
            if (_running) ForceFinish();   // teardown mid-sequence: never leave the felt frozen
        }

        // Reconnect: a resync board re-pushes whatever state the round is in. Re-seed the transition baseline from the
        // CURRENT board, so an already-settled resync board isn't mistaken for a fresh in-round→settle transition —
        // which would REPLAY the whole reveal/draws/chips for a round the server already resolved.
        private void OnConnection(bool connected)
        {
            if (connected && !_running && table != null)
                _prevInProgress = table.Board != null && table.Board.RoundInProgress;
        }

        // The ONLY trigger. Runs synchronously inside TableController's OnBoardChanged fan-out (same frame as Render).
        // BeginSequence's first act cancels the view's just-armed deferred sweep, before Update() ever sees it.
        private void OnBoard(BoardSnapshot board)
        {
            if (board == null) return;
            bool nowIn = board.RoundInProgress;
            bool wasIn = _prevInProgress;
            _prevInProgress = nowIn;
            if (_running) return;                        // owning a sequence → ignore re-pushes / reconnects
            if (wasIn && !nowIn) BeginSequence(board);   // in-round → settle: the transition (never a cold join)
        }

        private void BeginSequence(BoardSnapshot board)
        {
            _running = true;
            _board = board;
            _flipDone = _payShown = _finaleDone = false;

            // FREEZE (synchronous, no yield before these):
            if (view != null)
            {
                view.BeginRoundEnd();              // cancel the deferred sweep + latch RoundEndSettling
                view.ShowFinalPlayerHands(board);  // snap any round-ender card the gated Render skipped
            }
            // BetStacks is already held intrinsically (we registered at OnEnable) — no synchronous call needed.

            _seq = StartCoroutine(RunSequence());
            _watchdog = StartCoroutine(Watchdog());
        }

        private IEnumerator RunSequence()
        {
            var board = _board;

            // Guard: let the opening-deal pump FINISH before we touch the dealer — otherwise the reveal interrupts her
            // mid-throw and cards strand. On a natural blackjack the pump is still going when this settle push lands, so
            // this legitimately waits several throws; cap generously (the watchdog covers a true hang).
            Kick();
            float t = 0f;
            while (dealer != null && dealer.Busy && t < maxHoldSeconds) { t += Time.unscaledDeltaTime; yield return null; }

            // A natural blackjack / 21 settles the round the instant the opening cards are dealt — so the deal pump can
            // get cut off before it throws every card (leaving a card PARKED HIDDEN → the player shows only 1 card).
            // Snap any still-parked opening card into place now, so both hands are complete before the reveal. No-op when
            // the pump already threw everything.
            if (view != null) view.SnapParkedCards();

            bool hasDealer = board.Dealer != null && board.Dealer.Cards != null && board.Dealer.Cards.Count > 1;

            // 0) PEEK (only if it never ran this round). A dealer BLACKJACK settles the round INSTANTLY, so the
            // mid-round peek trigger never gets a window — play it here so she's SEEN to check before turning the card
            // over, instead of the blackjack just appearing. No-op when she already peeked, or her up-card never
            // warranted one.
            if (peekDriver != null) { Kick(); yield return peekDriver.PeekIfMissed(board); }

            // 1) REVEAL — the clip event fires FlipHole; the fallback fires it if the clip / event is missing.
            if (hasDealer) { Kick(); yield return PlayBody(reveal, FlipHole); yield return WaitSettle(); }

            // 2) DRAWS — park the beyond-opening cards, then throw them one at a time.
            if (hasDealer && view != null)
            {
                Kick();
                int draws = view.LayOutDealerFinal(board.Dealer);
                if (draws > 0 && dealer != null) yield return dealer.ThrowDealerDraws(draws);
                yield return WaitSettle();
            }

            // 3) HOLD
            Kick();
            if (holdSeconds > 0f) yield return new WaitForSecondsRealtime(holdSeconds);

            // 4) COLLECT — ONE loser seat at a time: the dealer plays THAT seat's collect gesture and its event flies the
            // seat's chips to her. Same one-at-a-time, event-coupled model as the deal throws.
            foreach (int seat in LoserSeats())
            {
                Kick();
                if (dealer != null) yield return dealer.CollectFromSeat(seat, () => CollectSeat(seat));
                else CollectSeat(seat);
                yield return new WaitForSecondsRealtime(chipFlightSeconds + payGap);
            }
            if (dealer != null) dealer.ReturnToIdle();

            // 5) PAY — ONE winner seat at a time: the dealer plays THAT seat's pay gesture and its event flies the
            // winnings out to that seat.
            foreach (var r in WinnerResults())
            {
                Kick();
                if (dealer != null) yield return dealer.PayToSeat(r.SeatNumber, () => PaySeat(r));
                else PaySeat(r);
                yield return new WaitForSecondsRealtime(chipFlightSeconds + payGap);
            }
            if (dealer != null) dealer.ReturnToIdle();

            // The winnings have landed → NOW reveal the credited balances (seat plates + banner + win juice). Held until
            // here so nothing shows the payout before the dealer paid — the whole point of the sequence.
            RevealPayout();

            // 6) SWEEP
            Kick();
            if (view != null) view.SweepNow();
            yield return new WaitForSecondsRealtime(view != null ? view.SweepDuration : 0.5f);

            // 7) FINALE — the last beat, after the cards are collected. Betting reopens the moment this returns (its
            // RoundFinished event, else the clip ending). Complete() is NOT gated on it — it simply runs next.
            Kick();
            yield return PlayFinale();

            // 8) DONE
            Complete();
        }

        // Plays a body clip on the dealer and awaits its end; the clip's authored event fires the beat mid-clip. Always
        // calls the beat afterward as an idempotent fallback (missing / unauthored event or a null clip).
        private IEnumerator PlayBody(ClipTransition clip, System.Action beat)
        {
            if (dealer != null && clip != null && clip.Clip != null)
                yield return dealer.PlayBodyClip(clip);
            beat?.Invoke();
        }

        /// <summary>
        /// FINALE beat: play the last clip DETACHED and hold only until its RoundFinished event — so betting reopens on
        /// the frame you authored, while the clip plays its tail out. Falls back to the clip's length if no event fires,
        /// and returns immediately with no clip, so the round can never hang here.
        /// </summary>
        private IEnumerator PlayFinale()
        {
            _finaleDone = false;
            if (dealer == null || finale == null || finale.Clip == null) yield break;   // no clip → betting reopens now

            dealer.PlayBodyClipDetached(finale);
            // No event bound → fall back to the clip's REAL duration (honouring its Speed), with no extra padding, so
            // betting reopens exactly when the clip ends rather than a beat later.
            float timeout = finale.Clip.length / Mathf.Max(0.01f, finale.Speed);
            float t = 0f;
            while (!_finaleDone && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
        }

        /// <summary>FINALE event — bind this on the finale clip's "round is over" frame. Betting reopens from here.</summary>
        public void RoundFinished() => _finaleDone = true;

        private IEnumerator WaitSettle()
        {
            float t = 0f;
            while (view != null && view.AnyCardAnimating() && t < settleTimeout)
            { t += Time.unscaledDeltaTime; yield return null; }
        }

        // ---- beat callbacks (bound to each clip's Animancer event; idempotent) ----

        /// <summary>REVEAL beat: turn the dealer hole card edge-on, swap the art at the apex, turn it flat.</summary>
        public void FlipHole()
        {
            if (_flipDone) return; _flipDone = true;
            if (view == null || _board?.Dealer?.Cards == null || _board.Dealer.Cards.Count < 2) return;
            var revealed = _board.Dealer.Cards[1];
            var card = view.DealerHoleCard();
            if (card == null) { view.RevealDealerHole(revealed); return; }   // no visible card → just fix the data
            // Juicy flip: edge-on (ease-in) → swap art + data at the apex → back with ease-out-back overshoot + scale
            // pop + lift. The reveal is the ONE notable flip per round, so it earns the full treatment. Time + edge axis
            // come from this director; the lift/pop/overshoot juice is authored on the DEALER (Card Juice) so the reveal
            // and the peek share one tuning block.
            var flip = CardFlip.On(card.gameObject);
            var juice = dealer != null ? dealer.CardJuice : null;   // null → CardFlip's built-in defaults
            _flip = StartCoroutine(flip.Reveal(() => view.RevealDealerHole(revealed), flipSeconds, flipEdgeEuler, juice));
        }

        // Losers / winners in SEAT order (players only; the dealer is seat 0). The collect / pay loops drive one at a
        // time, each seat's clip event flying that seat's chips.
        private IEnumerable<int> LoserSeats()
        {
            if (_board?.LastResults == null) yield break;
            foreach (var r in _board.LastResults)
                if (r != null && r.Outcome == "lose") yield return r.SeatNumber;
        }

        private IEnumerable<SeatResultView> WinnerResults()
        {
            if (_board?.LastResults == null) yield break;
            foreach (var r in _board.LastResults)
                if (r != null && r.Outcome == "win" && (long)r.Delta > 0) yield return r;
        }

        // The dealer-hand hub chips gather TO / pay FROM. Preference: an explicit anchor → her chips-in-hand prop point
        // (so real chips leave/enter exactly where the prop showed them) → the view's deal source (her hand) as a last
        // resort. So collect/pay fly from HER HAND with zero extra wiring.
        private Transform ChipHub =>
              dealerHandAnchor != null ? dealerHandAnchor
            : (dealer != null && dealer.ChipHandPoint != null) ? dealer.ChipHandPoint
            : (view != null ? view.DealSource : null);

        /// <summary>COLLECT one loser seat: its committed stacks (both hands, split-aware) fly to the dealer's hand. Fired
        /// by that seat's collect clip event.</summary>
        private void CollectSeat(int seat)
        {
            var hub = ChipHub;
            if (betStacks == null || hub == null) return;
            var chips = betStacks.DetachSeatStacks(seat);
            for (int i = 0; i < chips.Count; i++)
                FlyChip(chips[i], hub.position, chipFlightSeconds);
        }

        /// <summary>PAY one winner seat: winnings (= LastResults.Delta, never inferred) fly from the dealer's hand to the
        /// seat's anchor. Fired by that seat's pay clip event.</summary>
        private void PaySeat(SeatResultView r)
        {
            var hub = ChipHub;
            if (betStacks == null || hub == null || r == null) return;
            long amount = (long)r.Delta;               // net winnings the dealer pushes over
            if (amount <= 0) return;
            var target = betStacks.ChipAnchor(r.SeatNumber);
            if (target == null) return;                // seat beyond authored anchors → skip
            // Build the winnings UNDER the seat anchor (the same unit-scale parent the VISIBLE bet chips use) but START
            // them at the dealer's hand, then fly them to the seat. Parenting straight to the hub inherited its scale —
            // and when the hub is a card model (tiny scale) the chips were built invisibly small.
            Vector3 startLocal = target.InverseTransformPoint(hub.position);
            var chips = betStacks.BuildLooseStack(target, startLocal, amount);
            for (int i = 0; i < chips.Count; i++)
                FlyChip(chips[i], target.position, chipFlightSeconds);
        }

        // Fly one chip to a world target via CardMover (local-space ease), then destroy it. Tracked so a mid-flight
        // teardown (ForceFinish) can reclaim it.
        private void FlyChip(GameObject chip, Vector3 worldTarget, float seconds)
        {
            if (chip == null) return;
            _ownedChips.Add(chip);
            var tr = chip.transform;
            var mover = chip.GetComponent<CardMover>() ?? chip.AddComponent<CardMover>();
            Vector3 local = tr.parent != null ? tr.parent.InverseTransformPoint(worldTarget) : worldTarget;
            mover.Target(local, tr.localRotation, tr.localScale, seconds);
            StartCoroutine(DestroyAfter(chip, seconds + 0.05f));
        }

        private IEnumerator DestroyAfter(GameObject go, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            _ownedChips.Remove(go);
            if (go != null) Destroy(go);
        }

        /// <summary>Reveal the credited balances (seat plates un-hold + re-render; win juice bursts). Fired once per
        /// sequence, on the PAY beat — or on a force-finish, since the payout is real server-side regardless.</summary>
        private void RevealPayout()
        {
            if (_payShown) return; _payShown = true;
            if (seatPlates != null) seatPlates.RevealNow(_board);
            if (resultBanner != null) resultBanner.RevealNow(_board);
            if (winChipFly != null) winChipFly.PlayForLocalWin(_board);
        }

        // ---- termination (all idempotent via _running) ----

        private void Complete()
        {
            if (!_running) return;
            _running = false;
            _seq = null;
            RevealPayout();                                // safety: normally already fired in RunSequence, but idempotent
            if (_watchdog != null) { StopCoroutine(_watchdog); _watchdog = null; }
            if (view != null) view.EndRoundEnd();          // drop the gate (cards already swept in beat 6)
            if (dealer != null) dealer.ResetBaseline();    // conductor skipped every held push → re-baseline vs the next board
            if (betStacks != null) betStacks.ReleaseHold();
        }

        private void ForceFinish()
        {
            _running = false;
            if (_seq != null) { StopCoroutine(_seq); _seq = null; }
            if (_flip != null) { StopCoroutine(_flip); _flip = null; }   // orphaned flip could tug a card SweepNow just pooled
            if (_watchdog != null) { StopCoroutine(_watchdog); _watchdog = null; }
            for (int i = 0; i < _ownedChips.Count; i++) if (_ownedChips[i] != null) Destroy(_ownedChips[i]);
            _ownedChips.Clear();
            RevealPayout();                                // snap the payout in too — it happened server-side
            if (view != null) { view.SweepNow(); view.EndRoundEnd(); }
            if (dealer != null) { dealer.ReleaseChip(); dealer.ResetBaseline(); }   // fire any pending chip cb so a torn-down beat still flies
            if (betStacks != null) betStacks.ReleaseHold();
        }

        // Kicked at the start of EACH beat, so the cap bounds a single WEDGED beat — not the (legitimately long) sum of
        // reveal + N draws + hold + per-loser collect + per-winner pay + sweep, which a fixed whole-sequence budget would truncate.
        private void Kick() => _watchdogDeadline = Time.unscaledTime + Mathf.Max(1f, maxHoldSeconds);

        private IEnumerator Watchdog()
        {
            Kick();
            while (_running)
            {
                if (Time.unscaledTime >= _watchdogDeadline) { ForceFinish(); yield break; }   // one beat wedged → bail
                yield return null;
            }
        }
    }
}
