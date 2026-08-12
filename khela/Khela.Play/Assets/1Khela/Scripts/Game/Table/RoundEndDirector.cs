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
    /// Money-safety: every chip visual is driven by <see cref="BoardSnapshot.LastResults"/> — the server is
    /// authoritative on balances; the chips never imply a payout the server didn't make. COLLECT and PAY read the
    /// server's PER-HAND results (<c>SeatResultView.Hands</c>), so a split settles hand by hand exactly like two
    /// single hands, each at its own chip spot; the seat-level Outcome/Delta is only the fallback for an older
    /// server that sends no per-hand list.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoundEndDirector : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private TableController table;
        [SerializeField] private BlackjackTableView view;
        [SerializeField] private DealerAnimator dealer;
        [SerializeField] private BetStacks betStacks;
        [Tooltip("Seat banner cards — ALL of them. There is one SeatPlates per per-seat HUD layout and only the local " +
                 "seat's layout is active, so every one must be held or a remote seat's chip count jumps at settle. " +
                 "Auto-found (including inactive) if empty.")]
        [SerializeField] private SeatPlates[] seatPlates;
        [Tooltip("Win juice (chips → balance icon). Fired on the PAY beat instead of the raw settle push. Optional.")]
        [SerializeField] private WinChipFly winChipFly;
        [Tooltip("Per-hand WIN / PUSH / LOSE (and the dealer's mirrored win/lose) card banners. Held until the reveal + " +
                 "draws are done, so they don't announce the outcome mid-deal. Optional (auto-found).")]
        [SerializeField] private HandBlackjackLabels handLabels;
        [Tooltip("Chip-count roll/punch juice. Held so a WIN doesn't tick the number up at the settle push — the count " +
                 "rises as the flying chips land, and this beat flushes any remainder. Optional (auto-found).")]
        [SerializeField] private ChipCountJuice countJuice;
        [Tooltip("Your own banner — celebrates on the same PAY beat as the seat plates. Auto-found.")]
        [SerializeField] private LocalPlayerBanner localBanner;
        [Tooltip("Dealer peek driver. On a dealer BLACKJACK the round settles instantly, so its mid-round trigger never " +
                 "fires — this director then plays the peek just before the reveal. Optional (auto-found).")]
        [SerializeField] private DealerPeek peekDriver;
        [Tooltip("Table sound. The hole-card reveal is fired from the flip beat itself so it can't drift from the " +
                 "animation. Optional (auto-found).")]
        [SerializeField] private PlayCard.Audio.TableAudio tableAudio;
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
        private BoardSnapshot _pendingSettle;   // a settle that landed while we were mid-sequence — run it after
        private readonly List<GameObject> _ownedChips = new List<GameObject>();   // director-owned flying chips

        /// <summary>True while the director owns the round-end (for optional HUD gating / tests).</summary>
        public bool Holding => _running;

        private void OnEnable()
        {
            // NOTE the FindObjectsInactive.Include: the payout-gated UI lives on objects that are DISABLED at the moment
            // this runs — e.g. the seat plates sit inside per-seat HUD layouts and only the local seat's is active. The
            // default overload skips inactive objects, so it silently resolved to null / the wrong instance and the
            // whole payout hold was never wired.
            if (table == null) table = FindAnyObjectByType<TableController>(FindObjectsInactive.Include);
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>(FindObjectsInactive.Include);
            if (dealer == null) dealer = FindAnyObjectByType<DealerAnimator>(FindObjectsInactive.Include);
            if (betStacks == null) betStacks = FindAnyObjectByType<BetStacks>(FindObjectsInactive.Include);
            seatPlates = ResolveSeatPlates(seatPlates);
            if (winChipFly == null) winChipFly = FindAnyObjectByType<WinChipFly>(FindObjectsInactive.Include);
            if (handLabels == null) handLabels = FindAnyObjectByType<HandBlackjackLabels>(FindObjectsInactive.Include);
            if (countJuice == null) countJuice = FindAnyObjectByType<ChipCountJuice>(FindObjectsInactive.Include);
            if (localBanner == null) localBanner = FindAnyObjectByType<LocalPlayerBanner>(FindObjectsInactive.Include);
            if (peekDriver == null) peekDriver = FindAnyObjectByType<DealerPeek>(FindObjectsInactive.Include);
            if (tableAudio == null) tableAudio = FindAnyObjectByType<PlayCard.Audio.TableAudio>(FindObjectsInactive.Include);

            if (table != null)
            {
                table.OnBoardChanged += OnBoard;
                table.OnConnectionChanged += OnConnection;
                _prevInProgress = table.Board != null && table.Board.RoundInProgress;   // seed: no false trigger on a cold join
            }
            if (betStacks != null) betStacks.RegisterSettleDirector(this);   // arms BetStacks' INTRINSIC hold
            if (seatPlates != null)                                          // arms EVERY seat-plate balance hold (revealed on PAY)
                foreach (var sp in seatPlates) if (sp != null) sp.RegisterSettleDirector(this);
            if (winChipFly != null) winChipFly.RegisterSettleDirector(this); // win juice now fires on PAY, not the settle push
            if (handLabels != null) handLabels.RegisterSettleDirector(this); // per-hand + dealer win/lose banners held until PAY
            if (countJuice != null) countJuice.RegisterSettleDirector(this); // chip count won't tick up until chips land
        }

        /// <summary>
        /// Resolve the seat-plate drivers, tolerating a HALF-AUTHORED array.
        ///
        /// The old test was <c>Length == 0</c>, and the scene held an array of length ONE whose single element was
        /// empty — so the auto-find was skipped and the director ran with nothing but a null. Every seat-plate feature
        /// silently did nothing (the balance hold, and the win FX), while <see cref="localBanner"/> — a single
        /// reference, which DOES fall back when null — kept working. That asymmetry is exactly "my own celebration
        /// fires, the other players' never do". Drop the nulls, and fall back to the scene sweep when nothing real is
        /// authored, so neither an empty array nor an array of empty slots can disable this again.
        /// </summary>
        private static SeatPlates[] ResolveSeatPlates(SeatPlates[] authored)
        {
            if (authored != null)
            {
                var kept = new List<SeatPlates>(authored.Length);
                foreach (var sp in authored) if (sp != null) kept.Add(sp);
                if (kept.Count > 0) return kept.ToArray();
            }
            return FindObjectsByType<SeatPlates>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        private void OnDisable()
        {
            if (table != null) { table.OnBoardChanged -= OnBoard; table.OnConnectionChanged -= OnConnection; }
            if (betStacks != null) betStacks.UnregisterSettleDirector(this);
            if (seatPlates != null)
                foreach (var sp in seatPlates) if (sp != null) sp.UnregisterSettleDirector(this);
            if (winChipFly != null) winChipFly.UnregisterSettleDirector(this);
            if (handLabels != null) handLabels.UnregisterSettleDirector(this);
            if (countJuice != null) countJuice.UnregisterSettleDirector(this);
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
            bool settled = wasIn && !nowIn;              // in-round → settle: the transition (never a cold join)

            if (_running)
            {
                // A settle arrived while we still own the felt. _prevInProgress has ALREADY consumed the edge above,
                // so simply returning loses it forever and that round's reveal/collect/pay never plays. Latch it and
                // run it when this sequence finishes. Reachable whenever rounds come fast — most easily after a
                // natural, whose ceremony can still be running when the next round deals and settles.
                if (settled) _pendingSettle = board;
                return;
            }

            if (settled) BeginSequence(board);
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
            // this legitimately waits several throws.
            //
            // The wait must be SHORTER than the watchdog budget, and must keep kicking it. Both used to be
            // maxHoldSeconds with no re-Kick, so a long pump could burn the entire per-beat deadline here — the
            // watchdog then ForceFinish'd the sequence before COLLECT/PAY ever ran, snapping the payout with no chip
            // animation at all. That is the "blackjack breaks the chip animation" case: a natural is exactly when
            // this wait is longest, because the settle arrives mid-pump.
            Kick();
            float pumpWait = Mathf.Max(0.5f, maxHoldSeconds * 0.5f);
            float t = 0f;
            while (dealer != null && dealer.Busy && t < pumpWait)
            {
                t += Time.unscaledDeltaTime;
                Kick();               // progressing, not hung — don't let the watchdog count this against the beat
                yield return null;
            }

            // The director owns her body from here. Waiting above is not enough: the peek/reveal below play on the SAME
            // Animancer, so a throw still running gets cut before its Throw event — the only thing that hides her
            // in-hand card prop and releases the parked card — leaving a card stuck in her hand while the felt already
            // shows the real one. A dealer BLACKJACK hits this every time (the round settles the instant her second
            // card is dealt, so we cut in while she is throwing to herself). Stop the pump cleanly; the snap below
            // puts down anything it was still holding.
            if (dealer != null) dealer.AbortThrows();

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

            // 3) HOLD — the dealer's final hand is now fully shown, so this is the point where "all the dealing has
            // settled". Reveal the per-hand WIN / PUSH / LOSE card banners (and the dealer's mirrored win/lose) HERE and
            // let holdSeconds read them before the chips move: earlier would announce the outcome mid-deal (the reported
            // bug); waiting until after the PAY beat would flash them for only the sweep, since they're
            // pinned to the cards and vanish when the felt is swept. (RevealPayout re-calls this idempotently, as a
            // safety net for force-finish paths that skip this beat.)
            if (handLabels != null) handLabels.RevealNow(_board);

            // The win celebration starts WITH that badge — same beat, same moment the player learns they won. It used
            // to start at RevealPayout, which is two beats later (after collect and pay), so it appeared long after
            // the badge it is supposed to accompany.
            ShowWinFx();

            Kick();
            if (holdSeconds > 0f) yield return new WaitForSecondsRealtime(holdSeconds);

            // 4) COLLECT — ONE losing HAND at a time: the dealer plays that seat's collect gesture and its event flies
            // THAT hand's chips to her (a split's hands are collected separately, so a winning hand keeps its bet).
            // Same one-at-a-time, event-coupled model as the deal throws.
            foreach (var h in LoserHands())
            {
                Kick();
                var hand = h;   // capture per iteration — the closure must not see the loop's last value
                if (dealer != null) yield return dealer.CollectFromSeat(hand.Seat, () => CollectHand(hand));
                else CollectHand(hand);
                yield return new WaitForSecondsRealtime(chipFlightSeconds + payGap);
            }
            if (dealer != null) dealer.ReturnToIdle();

            // 5) PAY — ONE winning HAND at a time: the dealer plays that seat's pay gesture and its event flies the
            // winnings out to that HAND's chip spot (a split's two hands are paid separately, like two single hands).
            foreach (var h in WinnerHands())
            {
                Kick();
                var hand = h;
                if (dealer != null) yield return dealer.PayToSeat(hand.Seat, () => PayHand(hand));
                else PayHand(hand);
                yield return new WaitForSecondsRealtime(chipFlightSeconds + payGap);
            }
            if (dealer != null) dealer.ReturnToIdle();

            // The winnings have landed → NOW reveal the credited balances (seat plates + win juice). Held until
            // here so nothing shows the payout before the dealer paid — the whole point of the sequence.
            RevealPayout();

            // 6) SWEEP
            Kick();
            if (view != null) view.SweepNow();
            yield return new WaitForSecondsRealtime(view != null ? view.SweepDuration : 0.5f);

            // The celebration ends HERE, not before the sweep. RevealPayout only STARTS the chip count — WinChipFly
            // feeds it chip by chip and the number rolls for a while afterwards, so clearing any earlier cut the
            // particle off mid-payout, which is why it barely showed.
            ClearWinFx();

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
            // BEFORE the early-out below: a round where the card isn't on the felt still reveals, and going silent for
            // it would drop the sound exactly on the reconnect / mid-round-join cases.
            if (tableAudio != null) tableAudio.PlayDealerReveal();
            if (card == null) { view.RevealDealerHole(revealed); return; }   // no visible card → just fix the data
            // Juicy flip: edge-on (ease-in) → swap art + data at the apex → back with ease-out-back overshoot + scale
            // pop + lift. The reveal is the ONE notable flip per round, so it earns the full treatment. Time + edge axis
            // come from this director; the lift/pop/overshoot juice is authored on the DEALER (Card Juice) so the reveal
            // and the peek share one tuning block.
            var flip = CardFlip.On(card.gameObject);
            var juice = dealer != null ? dealer.CardJuice : null;   // null → CardFlip's built-in defaults
            _flip = StartCoroutine(flip.Reveal(() => view.RevealDealerHole(revealed), flipSeconds, flipEdgeEuler, juice));
        }

        /// <summary>One settled HAND the chips must move for: which seat, which hand (and how many hands that seat
        /// played, since the per-hand chip spot is a centred split offset), plus that hand's own net.</summary>
        private readonly struct HandRef
        {
            public readonly int Seat, HandIndex, HandCount;
            public readonly decimal Delta;
            public HandRef(int seat, int handIndex, int handCount, decimal delta)
            { Seat = seat; HandIndex = handIndex; HandCount = handCount; Delta = delta; }
        }

        // Losers / winners PER HAND, in seat order (players only; the dealer is seat 0). A split is settled hand by
        // hand — exactly like two separate single hands — so a seat that wins one and loses the other both pays and
        // collects, at each hand's own chip spot. Keying this off the seat NET (the old behaviour) meant one losing
        // hand swept the whole seat's chips ("one loss takes all"), and a mixed split that netted to zero moved no
        // chips at all while the per-hand banners announced a win and a loss.
        //
        // Falls back to ONE synthetic entry per seat, using the seat-level Outcome/Delta, when the server sends no
        // per-hand list (older server) — i.e. exactly the previous behaviour.
        private IEnumerable<HandRef> SettledHands(bool winners)
        {
            if (_board?.LastResults == null) yield break;
            foreach (var r in _board.LastResults)
            {
                if (r == null) continue;
                var hands = r.Hands;
                if (hands != null && hands.Count > 0)
                {
                    foreach (var h in hands)
                    {
                        if (h == null) continue;
                        long d = (long)h.Delta;
                        if (winners ? d > 0 : d < 0)
                            yield return new HandRef(r.SeatNumber, h.HandIndex, hands.Count, h.Delta);
                    }
                }
                else
                {
                    long d = (long)r.Delta;
                    bool match = winners ? (r.Outcome == "win" && d > 0) : (r.Outcome == "lose");
                    if (match) yield return new HandRef(r.SeatNumber, 0, 1, r.Delta);
                }
            }
        }

        private IEnumerable<HandRef> LoserHands() => SettledHands(winners: false);
        private IEnumerable<HandRef> WinnerHands() => SettledHands(winners: true);

        // The dealer-hand hub chips gather TO / pay FROM. Preference: an explicit anchor → her chips-in-hand prop point
        // (so real chips leave/enter exactly where the prop showed them) → the view's deal source (her hand) as a last
        // resort. So collect/pay fly from HER HAND with zero extra wiring.
        private Transform ChipHub =>
              dealerHandAnchor != null ? dealerHandAnchor
            : (dealer != null && dealer.ChipHandPoint != null) ? dealer.ChipHandPoint
            : (view != null ? view.DealSource : null);

        /// <summary>COLLECT one losing HAND: only THAT hand's committed stack flies to the dealer, so a split's other
        /// hand keeps its bet on the felt. Fired by that seat's collect clip event.</summary>
        private void CollectHand(HandRef h)
        {
            var hub = ChipHub;
            if (betStacks == null || hub == null) return;
            var chips = betStacks.DetachHandStack(h.Seat, h.HandIndex);
            for (int i = 0; i < chips.Count; i++)
                FlyChip(chips[i], hub.position, chipFlightSeconds);
        }

        /// <summary>PAY one winning HAND: its winnings (= that hand's own Delta, never inferred) fly from the dealer's
        /// hand to THAT hand's chip spot — the split position the hand's bet stack was built at. Fired by that seat's
        /// pay clip event.</summary>
        private void PayHand(HandRef h)
        {
            var hub = ChipHub;
            if (betStacks == null || hub == null) return;
            long amount = (long)h.Delta;               // net winnings the dealer pushes over, for this hand alone
            if (amount <= 0) return;
            var target = betStacks.ChipAnchor(h.Seat);
            if (target == null) return;                // seat beyond authored anchors → skip
            // Build the winnings UNDER the seat anchor (the same unit-scale parent the VISIBLE bet chips use) but START
            // them at the dealer's hand, then fly them to this HAND's spot. Parenting straight to the hub inherited its
            // scale — and when the hub is a card model (tiny scale) the chips were built invisibly small.
            Vector3 worldTarget = betStacks.HandChipPoint(h.Seat, h.HandIndex, h.HandCount);
            Vector3 startLocal = target.InverseTransformPoint(hub.position);
            var chips = betStacks.BuildLooseStack(target, startLocal, amount);
            for (int i = 0; i < chips.Count; i++)
                FlyChip(chips[i], worldTarget, chipFlightSeconds);

            // The chips ARRIVING is a separate beat from her pushing them — the push is heard at the release frame,
            // this is the winnings hitting the felt in front of the player. Every chip in a hand flies for the same
            // duration, so they land together: ONE sound per hand, not one per chip.
            if (tableAudio != null && chips.Count > 0)
                StartCoroutine(PayLandAfter(chipFlightSeconds, worldTarget));
        }

        private IEnumerator PayLandAfter(float delay, Vector3 worldTarget)
        {
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
            if (tableAudio != null) tableAudio.PlayChipsPayLand(worldTarget);
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
        // Starts the win celebration on every banner, at the HOLD beat — with the WIN badge, before the chips move.
        private void ShowWinFx()
        {
            if (seatPlates != null)
                foreach (var sp in seatPlates) if (sp != null) sp.ShowWinFx(_board);
            if (localBanner != null) localBanner.RevealNow(_board);
        }

        // Ends the win celebration on every banner. Called when the payout is finished (chips landed, count rolled)
        // and again on ForceFinish, so a torn-down sequence can never leave particles running over a swept felt.
        private void ClearWinFx()
        {
            if (seatPlates != null)
                foreach (var sp in seatPlates) if (sp != null) sp.ClearWinFx();
            if (localBanner != null) localBanner.ClearWinFx();
        }

        private void RevealPayout()
        {
            if (_payShown) return; _payShown = true;
            if (seatPlates != null)
                foreach (var sp in seatPlates) if (sp != null) sp.RevealNow(_board);
            if (handLabels != null) handLabels.RevealNow(_board);   // idempotent safety net; primary reveal is the HOLD beat
            if (winChipFly != null) winChipFly.PlayForLocalWin(_board);
            // Flush the chip count LAST: on a win WinChipFly is about to feed it chip by chip (that walk gets there on
            // its own), and on a push/loss — or with no WinChipFly wired — this is what stops the number lagging.
            if (countJuice != null) countJuice.RevealNow();
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
            ReapplyLatestBoard();
            RunPendingSettle();
        }

        // A settle that landed while we were mid-sequence was latched in OnBoard (the edge is consumed there, so it
        // can't be recovered from the board alone). Run it now that the felt is free, or that round would never get
        // its reveal/collect/pay.
        private void RunPendingSettle()
        {
            var pending = _pendingSettle;
            _pendingSettle = null;
            if (pending == null || _running) return;
            BeginSequence(pending);
        }

        /// <summary>
        /// Re-apply the CURRENT board after the hold drops. While the director owns the felt, Render / BetStacks /
        /// DealerAnimator all hard-return on incoming pushes — those snapshots are DISCARDED, not queued. Heads-up that
        /// is harmless (nothing arrives mid-ceremony), but with another player at the table the NEXT ROUND'S DEAL can
        /// land during our ceremony: without this the felt would stay empty until some later push happened to arrive.
        /// Transport is push-only, so we must replay it ourselves.
        /// </summary>
        private void ReapplyLatestBoard()
        {
            var board = table != null ? table.Board : null;
            if (board == null) return;
            if (view != null) view.Render(board);
            table.RepublishBoard();   // re-fan the snapshot so every OnBoardChanged consumer resyncs too
        }

        private void ForceFinish()
        {
            _running = false;
            ClearWinFx();   // a torn-down sequence must not leave particles running over a swept felt
            if (_seq != null) { StopCoroutine(_seq); _seq = null; }
            if (_flip != null) { StopCoroutine(_flip); _flip = null; }   // orphaned flip could tug a card SweepNow just pooled
            if (_watchdog != null) { StopCoroutine(_watchdog); _watchdog = null; }
            for (int i = 0; i < _ownedChips.Count; i++) if (_ownedChips[i] != null) Destroy(_ownedChips[i]);
            _ownedChips.Clear();
            RevealPayout();                                // snap the payout in too — it happened server-side
            if (view != null) { view.SweepNow(); view.EndRoundEnd(); }
            if (dealer != null) { dealer.ReleaseChip(); dealer.ResetBaseline(); }   // fire any pending chip cb so a torn-down beat still flies
            // ORDER MATTERS, and must match Complete(): release the hold BEFORE replaying. BetStacks hard-returns on
            // any push while held, so replaying first meant the replay was swallowed and the felt was left with no bet
            // stacks and no bet badges until some later server push happened along.
            if (betStacks != null) betStacks.ReleaseHold();
            ReapplyLatestBoard();
            RunPendingSettle();
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
