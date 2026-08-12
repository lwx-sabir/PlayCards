using System.Collections;
using System.Collections.Generic;
using Animancer;
using PlayCard.Audio;      // TableAudio — the dealer fires the shoe sound; the SoundEvent itself lives there
using PlayCard.Game.Cards;
using PlayCard.Game.Dtos;
using UnityEngine;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Drives the table dealer's BODY off the server board snapshots (Animancer, fully code-controlled). When a card is
    /// dealt to a seat it plays THAT seat's throw clip (Deal_Throw_P1/P2/P3); the dealer's own cards use
    /// <see cref="dealerDeal"/>. Cards are dealt one-by-one in real order, so throws are queued + staggered to match,
    /// then she returns to idle.
    ///
    /// It does NOT create or fly the card — <see cref="BlackjackTableView"/> owns that (point its dealSource at her hand
    /// and the real card leaves her hand). The only cosmetic here is the face-down <see cref="inHandCard"/> prop.
    ///
    /// TIMING IS SET ON THE CLIP, NOT HERE: put two events on each throw clip's Animancer preview — one where her hand
    /// reaches the shoe (bind <see cref="ShowInHandCard"/>) and one at release (bind <see cref="HideInHandCard"/>). You
    /// scrub the preview to place them, so you SEE the pose. This script just plays the right clip and returns to idle.
    ///
    /// Put on the dealer rig alongside <see cref="PlayCard.Avatar.DealerAvatarLoader"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DealerAnimator : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private AnimancerComponent animancer;
        [SerializeField] private TableController table;
        [Tooltip("The table view we conduct — cards fly on our throw releases. Auto-found if empty.")]
        [SerializeField] private BlackjackTableView tableView;

        [Header("Clips — add Pick/Throw events on each throw clip's preview (see class summary)")]
        [Tooltip("Looping idle (e.g. AS_Poker_Dealer_Idle_01). Loop Time = ON, Rig = Humanoid.")]
        [SerializeField] private ClipTransition idle;
        [Tooltip("One throw per seat. Element 0 = seat 1 (Deal_Throw_P1), 1 = seat 2, 2 = seat 3.")]
        [SerializeField] private ClipTransition[] seatDeals;
        [Tooltip("Throw for the DEALER's own cards (seat 0). Empty → falls back to seat 1's clip.")]
        [SerializeField] private ClipTransition dealerDeal;

        [Header("Peek — she checks the hole card when her up-card is a 10 or Ace (bind PeekHoleCard on the 'reads it' frame)")]
        [Tooltip("Dealer PEEK body clip — she leans in and secretly checks the face-down hole card. Bind PeekHoleCard on " +
                 "the frame she reads it, to tilt the hole card up toward her. Empty = the card juice fires immediately. " +
                 "Cosmetic only — the server already resolved dealer blackjack.")]
        [SerializeField] private ClipTransition peek;

        [Header("Card juice — ONE place to tune the flip + peek motion (used by the reveal AND the peek)")]
        [Tooltip("Motion applied to the CARD itself for the hole-card reveal flip and the peek tilt. Authored here " +
                 "rather than on the card prefab, since the dealer drives both. The card's runtime executor reads these.")]
        [SerializeField] private CardFlipTuning cardJuice = new CardFlipTuning();

        /// <summary>The card flip/peek motion settings, so the round-end director's reveal uses the SAME tuning as the
        /// peek — one place to author it.</summary>
        public CardFlipTuning CardJuice => cardJuice;

        [Header("Round-end per-seat clips — like seatDeals, but for the settle. Bind ReleaseChip on each (grab/push frame)")]
        [Tooltip("COLLECT gesture per LOSER seat (reach to seat N, sweep its chips in). Element 0 = seat 1. Bind ReleaseChip " +
                 "at the frame her hand grabs the stack — that flies the seat's chips to the dealer. Empty seat = fly immediately.")]
        [SerializeField] private ClipTransition[] seatCollects;
        [Tooltip("PAY gesture per WINNER seat (push chips out to seat N). Element 0 = seat 1. Bind ReleaseChip at the frame " +
                 "she pushes the chips out — that flies the winnings to the seat. Empty seat = fly immediately.")]
        [SerializeField] private ClipTransition[] seatPays;

        [Header("Timing")]
        [Tooltip("Playback speed of the throw clips. >1 if the pack's deal is too slow to keep up with dealing.")]
        [SerializeField] private float dealSpeed = 1f;
        [Tooltip("Extra pause AFTER each card is released, before the next throw starts. 0 = deal the next as soon as this one is thrown.")]
        [SerializeField] private float perCardStagger = 0.12f;
        [Tooltip("Safety: if a throw clip's Throw event never fires within this many seconds, release its card anyway so dealing can't stall. Only bites a clip missing its release event.")]
        [SerializeField] private float releaseTimeout = 4f;

        [Header("In-hand card props (optional)")]
        [Tooltip("RIGHT-hand card — parented under her right-hand bone, positioned in her grip. Shown when the card is " +
                 "passed to the right hand, hidden at THROW (that release flies the real card). Bind to the clip's " +
                 "pass/throw events via ShowInHandCard / HideInHandCard. Empty = none.")]
        [SerializeField] private GameObject inHandCard;
        [Tooltip("LEFT-hand card — parented under her left-hand bone. Shown when she PICKS from the machine, hidden when " +
                 "she PASSES it to the right hand. Bind to the clip's pickup/pass events via ShowLeftCard / HideLeftCard. " +
                 "Empty = none.")]
        [SerializeField] private GameObject inHandCardLeft;
        [Tooltip("RIGHT-hand card for the DEALER'S OWN deal clip ONLY — she grips it at a different angle there, so " +
                 "author a SECOND holder under the right hand with that rotation and assign it here. The clip binds the " +
                 "SAME ShowInHandCard/HideInHandCard; this one is picked automatically when she deals to herself " +
                 "(seat 0). Empty = reuse the normal right-hand card.")]
        [SerializeField] private GameObject inHandCardDealerOwn;
        [Tooltip("Chips-in-hand prop for the COLLECT/PAY (give) gestures — parented under her hand. Bind ShowDealHandChips " +
                 "on the clip's SCOOP frame (she picks the chips up) and it hides at ReleaseChip (she pushes them out → " +
                 "the real chips fly). The round-end director also flies the real chips FROM this point. Empty = none.")]
        [SerializeField] private GameObject dealHandChips;

        // Per-seat card totals from the last board (seat 0 = dealer). Null until the first snapshot sets the baseline,
        // so joining mid-round doesn't fire a burst of phantom deals.
        private Dictionary<int, int> _counts;
        private readonly Queue<int> _throwQueue = new Queue<int>();
        private readonly List<(int seat, int order)> _newThisBoard = new List<(int, int)>();
        private Coroutine _pump;
        private bool _pumping;   // running-flag for Pump — a dedicated bool, NOT the StartCoroutine handle, so a Pump that finishes synchronously (perCardStagger 0) can't wedge it permanently non-null

        // The seat the CURRENT throw is dealing to, and whether that throw's ONE parked card has been released yet. A
        // clip's release event is parameterless, so it recovers the seat from here; the flag makes release exactly-once
        // and lets us fire a safety-net release for a seat whose clip lacks (or omits) the event.
        private int _currentThrowSeat = -1;
        private bool _released;

        // Round-end per-seat COLLECT/PAY: the current seat's clip fires ReleaseChip at its grab/push frame, which invokes
        // this callback (set by the director) to fly THAT seat's chips — exactly how a throw's event releases its card.
        private System.Action _chipRelease;
        private bool _chipReleased;

        private void Awake()
        {
            if (animancer == null) animancer = GetComponentInChildren<AnimancerComponent>(true);
            if (table == null) table = FindAnyObjectByType<TableController>();
            if (tableView == null) tableView = FindAnyObjectByType<BlackjackTableView>();
            HideProp();
        }

        private void OnEnable()
        {
            if (table != null) { table.OnBoardChanged += OnBoard; table.OnConnectionChanged += OnConnection; }
            else Debug.LogWarning("[DealerAnimator] no TableController found — dealer won't animate to the board.");
            if (tableView != null) tableView.RegisterConductor(this);   // become the deal conductor: cards fly on our throws
            PlayIdle();
        }

        private void OnDisable()
        {
            if (table != null) { table.OnBoardChanged -= OnBoard; table.OnConnectionChanged -= OnConnection; }
            if (tableView != null) tableView.UnregisterConductor(this);   // gone → the view reverts to its own staggered deal
            if (_pump != null) { StopCoroutine(_pump); _pump = null; }
            _pumping = false;
        }

        // Reconnect/resync: the client MISSED boards during the blip, so our per-seat count baseline can disagree with
        // the view's per-card park diff on the single resync board (surplus parked cards would strand → AnyCardAnimating
        // latches on → the local player freezes). Drop the baseline so the next board takes the _counts==null path — it
        // fires NO throws and calls SnapParkedCards, revealing whatever the view parked. No divergence, no strand.
        private void OnConnection(bool connected)
        {
            if (connected) _counts = null;
        }

        /// <summary>Re-baseline to EMPTY on round-end release (not null). Empty means the next round's opening deal is
        /// seen as fresh cards and CONDUCTED (thrown one-by-one) on every client — including a remote player who never
        /// bet, who otherwise had no between-rounds push to clear a null baseline and so SNAPPED the whole deal at once.
        /// A genuine reconnect still nulls the baseline (see <see cref="OnConnection"/>) so it snaps to whatever the view
        /// parked. Empty is also strand-safe: any cards parked from a deal that landed during the hold count as new
        /// against the empty baseline, so they're thrown (released) rather than left stranded.</summary>
        public void ResetBaseline() => _counts = new Dictionary<int, int>();

        private void OnBoard(BoardSnapshot board)
        {
            if (board == null) return;
            // While a RoundEndDirector owns the felt the view freezes Render (parks nothing). Skip pushes too, so we
            // don't advance our per-seat baseline off pushes the view dropped — the director re-baselines us on release
            // (ResetBaseline) so the first post-hold board reconciles cleanly instead of stranding parked cards.
            if (tableView != null && tableView.RoundEndHeld) return;
            // Reset the baseline to EMPTY between rounds, in lockstep with BlackjackTableView.Render (which lays cards
            // out only while RoundInProgress). The server leaves the settled hands on the board until the next deal, so
            // counting them would keep a stale high baseline and the next round's fresh 2-card hands (2 < prevTotal)
            // would emit ZERO throws → the view's parked cards would never release.
            var now = board.RoundInProgress ? TallySeats(board) : new Dictionary<int, int>();

            if (_counts != null)   // baseline set → any per-seat increase is a freshly dealt card
            {
                _newThisBoard.Clear();
                foreach (var kv in now)
                {
                    _counts.TryGetValue(kv.Key, out int prev);
                    for (int c = prev; c < kv.Value; c++) _newThisBoard.Add((kv.Key, DealOrder(kv.Key, c)));
                }
                if (_newThisBoard.Count > 0)
                {
                    _newThisBoard.Sort((a, b) => a.order.CompareTo(b.order));   // players right-to-left (highest seat first), dealer last
                    foreach (var n in _newThisBoard) _throwQueue.Enqueue(n.seat);
                    if (!_pumping) { _pumping = true; _pump = StartCoroutine(Pump()); }
                }
            }
            else if (tableView != null)
            {
                // First board = our baseline (no throws). If we JOINED mid-round, the view already parked its existing
                // cards this same push (Render ran first) — snap them into view now so a rejoin shows the hand at once.
                tableView.SnapParkedCards();
            }
            _counts = now;
        }

        private static Dictionary<int, int> TallySeats(BoardSnapshot board)
        {
            var d = new Dictionary<int, int> { [0] = board.Dealer?.Cards?.Count ?? 0 };
            if (board.Seats != null)
                foreach (var seat in board.Seats)
                {
                    if (seat?.Player?.Hands == null) continue;
                    int n = 0;
                    foreach (var hand in seat.Player.Hands) n += hand?.Cards?.Count ?? 0;
                    d[seat.SeatNumber] = n;
                }
            return d;
        }

        // Real dealing order: round by round (cardIndex), players (seat asc) then the dealer (seat 0) LAST.
        // RIGHT-to-left within each round (highest seat = first base = dealt first), dealer (seat 0) last. Higher seat
        // number => smaller order => earlier. Must match BlackjackTableView.DealOrder + the server's GetOrderedHands.
        private static int DealOrder(int seat, int cardIndex) => cardIndex * 1000 + (seat == 0 ? 999 : 100 - seat);

        private IEnumerator Pump()
        {
            while (_throwQueue.Count > 0)
            {
                yield return ThrowOne(_throwQueue.Dequeue());   // plays the throw AND waits for it to release its card
                if (perCardStagger > 0f) yield return new WaitForSecondsRealtime(perCardStagger);
            }
            _pumping = false;
            PlayIdle();   // queue drained → back to idle (throws no longer set OnEnd, so we return here)
        }

        // ---- Round-end director hooks (driven by RoundEndDirector, NOT the board — its settle tally is empty) ----

        /// <summary>True while the opening-deal pump is still throwing; the round-end director waits on this before it drives the reveal.</summary>
        public bool Busy => _pumping;

        /// <summary>
        /// Hand the dealer's body over to the round-end director: stop dealing immediately and leave her CLEAN.
        ///
        /// Waiting on <see cref="Busy"/> is not enough on its own. The director drives the same Animancer
        /// (<see cref="PlayPeek"/>, <see cref="PlayBodyClip"/>), so any throw still in flight when it starts gets cut
        /// mid-clip — and a cut throw never reaches its Throw event, which is the ONE place that hides the in-hand prop
        /// and releases the parked card. The prop then stays visibly stuck in her hand while the felt already shows the
        /// real card. A dealer BLACKJACK hits this every time: the round settles the instant her second card is dealt,
        /// so the settle push lands while she is still throwing to herself.
        ///
        /// Flushes the cut throw's card rather than dropping it (the director's SnapParkedCards puts down anything
        /// still queued), then returns to idle. Idempotent — a no-op once the pump has drained normally.
        /// </summary>
        public void AbortThrows()
        {
            if (_pump != null) { StopCoroutine(_pump); _pump = null; }   // stops the nested ThrowOne with it
            _pumping = false;
            _throwQueue.Clear();

            ReleaseCurrent();          // BEFORE the resets below — it early-outs on _released / seat < 0
            _currentThrowSeat = -1;
            _released = true;

            PlayIdle();                // hides every in-hand prop, then returns her to idle
        }

        /// <summary>
        /// Throw the dealer's own draws (seat 0) ONE AT A TIME for the round-end reveal. Each throw releases exactly one
        /// card the view parked via <see cref="BlackjackTableView.LayOutDealerFinal"/>, reusing the SAME throw path (and
        /// release timeout) as the opening deal, then returns to idle. Yields until every draw is thrown; no-op for 0.
        /// </summary>
        public IEnumerator ThrowDealerDraws(int count)
        {
            if (count <= 0) yield break;
            for (int i = 0; i < count; i++)
            {
                yield return ThrowOne(0);   // plays seat 0's throw AND waits for it to release its one parked card
                if (perCardStagger > 0f) yield return new WaitForSecondsRealtime(perCardStagger);
            }
            PlayIdle();
        }

        /// <summary>
        /// Play a one-shot BODY clip (the director's reveal / gather / pay) and wait for it to finish. The clip's own
        /// Animancer event fires the director's beat mid-clip — exactly like the throw clips fire ours — so this just
        /// drives the animation and returns the dealer to idle. No-op (returns immediately) if the clip is missing, so
        /// the director's fallback beat still runs.
        /// </summary>
        public IEnumerator PlayBodyClip(ClipTransition clip)
        {
            if (animancer == null || clip == null || clip.Clip == null) yield break;
            var state = animancer.Play(clip);        // the clip's serialized events (bound to the director) fire as it plays
            state.Speed = Mathf.Max(0.01f, clip.Speed);   // honour the clip's authored Speed field
            float timeout = clip.Clip.length + 0.5f;
            float t = 0f;
            while (state.NormalizedTime < 1f && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            PlayIdle();
        }

        /// <summary>
        /// Play LOSER seat N's COLLECT gesture; its ReleaseChip event fires <paramref name="onGrab"/> (that seat's chips
        /// fly to the dealer). Waits until the event (or a safety timeout), then returns — the director paces the gap to
        /// the next seat and idles at the end. No clip for the seat → fires immediately so a settle can never stall.
        /// One seat at a time, event-coupled — the SAME model as the deal throws.
        /// </summary>
        public IEnumerator CollectFromSeat(int seat, System.Action onGrab)
        {
            _chipIsPay = false;   // so ReleaseChip's sound knows which gesture this is
            return PlaySeatChip(SeatClipFrom(seatCollects, seat), onGrab, showChips: false);
        }

        /// <summary>Play WINNER seat N's PAY gesture; its ReleaseChip event fires <paramref name="onPush"/> (winnings fly
        /// from the dealer to that seat). Same one-at-a-time, event-coupled model as <see cref="CollectFromSeat"/>.</summary>
        public IEnumerator PayToSeat(int seat, System.Action onPush)
        {
            _chipIsPay = true;
            return PlaySeatChip(SeatClipFrom(seatPays, seat), onPush, showChips: true);
        }

        // Which chip gesture is running. Collect and pay share ONE clip event (ReleaseChip), so the sound needs telling
        // them apart — and an explicit flag rather than reading the cosmetic showChips argument, which is about the
        // in-hand prop and could reasonably change without anyone thinking about audio.
        private bool _chipIsPay;

        /// <summary>Return the dealer to idle (the director calls this after the collect / pay loops drain).</summary>
        public void ReturnToIdle() => PlayIdle();

        /// <summary>
        /// Play the dealer PEEK gesture — she leans in and checks the hole card. Its authored PeekHoleCard event tilts
        /// the hole card up toward her; with no clip the card juice fires immediately. Yields until the clip finishes,
        /// then idle. Cosmetic — the server already resolved the dealer-blackjack rule.
        /// </summary>
        public IEnumerator PlayPeek()
        {
            if (animancer == null || peek == null || peek.Clip == null) { PeekHoleCard(); yield break; }
            var state = animancer.Play(peek);
            state.Speed = Mathf.Max(0.01f, peek.Speed);
            state.NormalizedTime = float.IsNaN(peek.NormalizedStartTime) ? 0f : peek.NormalizedStartTime;
            float timeout = peek.Clip.length + 0.5f;
            float t = 0f;
            while (state.NormalizedTime < 1f && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            PlayIdle();
        }

        /// <summary>Bind on the peek clip's 'reads it' frame: tilt/lift the hole card up TOWARD THE DEALER (card-owned
        /// juice), so the face is never flashed to the player. No-op if the hole card isn't on the felt.</summary>
        public void PeekHoleCard()
        {
            var hole = tableView != null ? tableView.DealerHoleCard() : null;
            if (hole == null) return;
            CardFlip.On(hole.gameObject).PlayPeek(cardJuice);
            // Sound rides the same frame as the tilt, so it can never drift from the animation.
            if (_audio == null) _audio = FindAnyObjectByType<TableAudio>(FindObjectsInactive.Include);
            if (_audio != null) _audio.PlayDealerPeek();
        }

        /// <summary>The chips-in-hand point (her hand) the round-end director flies real collect/pay chips FROM/TO, when
        /// no dedicated dealer-hand anchor is assigned — so the chips leave/enter exactly where the prop showed them.</summary>
        public Transform ChipHandPoint => dealHandChips != null ? dealHandChips.transform : null;

        /// <summary>Bind on a collect/pay clip's SCOOP frame: shows the chips-in-hand prop (she's holding the chips). It
        /// hides again at <see cref="ReleaseChip"/> (the push-out) — mirrors the deal's ShowInHandCard/HideInHandCard.</summary>
        public void ShowDealHandChips() { if (dealHandChips != null) dealHandChips.SetActive(true); }

        // Play one seat's collect/pay clip and hold until its ReleaseChip event flies that seat's chips (or the safety
        // timeout). Returns on release — the clip's tail plays on until the next Play/idle, exactly like ThrowOne.
        private IEnumerator PlaySeatChip(ClipTransition clip, System.Action onRelease, bool showChips)
        {
            _chipRelease = onRelease;
            _chipReleased = false;

            // Chips-in-hand prop for the PAY (give) gesture: show it for the WHOLE gesture so it no longer depends on a
            // ShowDealHandChips event being authored on THIS clip. It hides at ReleaseChip. Diagnostic tells us if the
            // prop is unassigned / can't activate / is scaled to nothing (the reasons it wouldn't be visible).
            if (showChips)
            {
                if (dealHandChips != null)
                {
                    dealHandChips.SetActive(true);
                    var pt = dealHandChips.transform;
                    Debug.Log($"[CHIPS-DIAG] dealHandChips shown → activeInHierarchy={dealHandChips.activeInHierarchy} " +
                              $"worldPos={pt.position} lossyScale={pt.lossyScale} (if activeInHierarchy=False a PARENT is off; " +
                              $"if lossyScale is tiny it's parented under a scaled bone)");
                }
                else Debug.LogWarning("[CHIPS-DIAG] 'Deal Hand Chips' is NOT assigned on the DealerAnimator — assign a chips model under her hand.");
            }

            if (animancer == null || clip == null || clip.Clip == null) { ReleaseChip(); yield break; }   // no clip → fly now

            var state = animancer.Play(clip);              // the clip's own ReleaseChip event (set on its preview) fires
            state.Speed = Mathf.Max(0.01f, clip.Speed);    // honour the clip's authored Speed field
            // Restart, same reason as ThrowOne: replaying a clip that's already at its end (Start Time = "continue from
            // current time") would skip its release event entirely.
            float startAt = clip.NormalizedStartTime;
            state.NormalizedTime = float.IsNaN(startAt) ? 0f : startAt;
            // Wait the WHOLE gesture (clip length) so an event authored near the end can fire — the old fixed
            // releaseTimeout (4s) cut off any clip whose release frame was later.
            float timeout = clip.Clip.length + 0.5f;
            float t = 0f;
            while (!_chipReleased && state.NormalizedTime < 1f && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            if (!_chipReleased) ReleaseChip();             // event missing → fly anyway
        }

        private static ClipTransition SeatClipFrom(ClipTransition[] arr, int seat)
        {
            int i = seat - 1;
            return (arr != null && i >= 0 && i < arr.Length) ? arr[i] : null;
        }

        /// <summary>Bind this to each collect/pay clip's event (the grab / push frame): flies the current seat's chips
        /// exactly once. Also fires as a safety-net for a missing clip/event so a settle can never stall.</summary>
        public void ReleaseChip()
        {
            if (dealHandChips != null) dealHandChips.SetActive(false);   // she let the chips go → hide the in-hand prop
            if (_chipReleased) return;
            _chipReleased = true;

            // On the release frame, so the chips are heard leaving her hands exactly as they visually do.
            if (_audio == null) _audio = FindAnyObjectByType<TableAudio>(FindObjectsInactive.Include);
            if (_audio != null) { if (_chipIsPay) _audio.PlayChipsPay(); else _audio.PlayChipsCollect(); }

            var cb = _chipRelease; _chipRelease = null;
            cb?.Invoke();
        }

        /// <summary>Play a one-shot BODY clip WITHOUT waiting for it — the caller decides when to move on (e.g. on the
        /// clip's own event). It returns to idle by itself when the clip ends. No-op if the clip is missing.</summary>
        public void PlayBodyClipDetached(ClipTransition clip)
        {
            if (animancer == null || clip == null || clip.Clip == null) return;
            StartCoroutine(PlayBodyClip(clip));
        }

        private ClipTransition ClipFor(int seat)
        {
            if (seat == 0) return (dealerDeal != null && dealerDeal.Clip != null) ? dealerDeal : SeatClip(1);
            return SeatClip(seat);
        }

        private ClipTransition SeatClip(int seat)
        {
            int i = seat - 1;
            return (seatDeals != null && i >= 0 && i < seatDeals.Length) ? seatDeals[i] : null;
        }

        // Play ONE seat's throw and WAIT until it RELEASES its card (the clip's Throw event → HideInHandCard →
        // ReleaseCurrent), so throws never overlap and each card flies at its OWN throw — THIS is what couples the card
        // to the animation. A clip-less seat, or a clip whose Throw event never fires within releaseTimeout, releases
        // immediately so a card can never stall.
        private IEnumerator ThrowOne(int seat)
        {
            _currentThrowSeat = seat;
            _released = false;
            _pickAnnounced = false;   // re-arm the shoe sound for THIS card

            var clip = ClipFor(seat);
            if (animancer == null || clip == null || clip.Clip == null)
            {
                ReleaseCurrent();   // no clip → deal it now (never stalls)
                yield break;
            }

            var state = animancer.Play(clip);              // the clip's own Pick/Throw events (set on its preview) fire
            // Honour the clip's AUTHORED Speed (the transition's Speed field) × the global dealSpeed multiplier — so
            // editing Speed on a deal clip actually takes effect instead of being overwritten. dealSpeed default 1 = no-op.
            state.Speed = Mathf.Max(0.01f, clip.Speed) * Mathf.Max(0.01f, dealSpeed);
            // RESTART every throw. The dealer's DRAWS replay the SAME clip back-to-back, and with the transition's Start
            // Time left as "continue from current time" (the default) the 2nd play RESUMES AT THE END — its release event
            // has already passed, so it never re-fires and the remaining cards all fall out together on the timeout.
            // (The opening deal hides this: consecutive throws use DIFFERENT per-seat clips.) Honour an explicit Start
            // Time if one is authored, else rewind to the top.
            float startAt = clip.NormalizedStartTime;
            state.NormalizedTime = float.IsNaN(startAt) ? 0f : startAt;
            // NOTE: do NOT touch state.Events here — it can clobber the clip's serialized Show/Throw events. Idle is
            // returned to by Pump when the queue drains.

            // Hold until the Throw event releases THIS card (or the safety timeout) — the next throw only starts after
            // this one has actually thrown, so cards can't stampede out ahead of the animation.
            float t = 0f;
            while (!_released && t < releaseTimeout) { t += Time.unscaledDeltaTime; yield return null; }
            // Safety net must do BOTH halves of the release, exactly like the clip event: a clip whose Throw event
            // never fires already showed the prop at its pass frame, so releasing the card without HideProp leaves a
            // card visibly stuck in her hand for the rest of the pump.
            if (!_released) { HideProp(); ReleaseCurrent(); }
        }

        private void PlayIdle()
        {
            HideProp();   // hide the cosmetic card ONLY — never release here (that's the throw event's job)
            if (animancer != null && idle != null && idle.Clip != null) animancer.Play(idle);
        }

        // Hide BOTH cosmetic in-hand props WITHOUT releasing a card. Used by idle/awake for a clean reset; the throw
        // event uses HideInHandCard (below) which also releases.
        private void HideProp()
        {
            if (inHandCard != null) inHandCard.SetActive(false);
            if (inHandCardLeft != null) inHandCardLeft.SetActive(false);
            if (inHandCardDealerOwn != null) inHandCardDealerOwn.SetActive(false);
            if (dealHandChips != null) dealHandChips.SetActive(false);   // reset the chips-in-hand prop on idle too
        }

        // The RIGHT-hand holder for the throw currently playing: the dealer's OWN deal grips the card at a different
        // angle, so it uses its own authored holder when one is assigned (seat 0 = dealing to herself).
        private GameObject RightHolder()
            => (_currentThrowSeat == 0 && inHandCardDealerOwn != null) ? inHandCardDealerOwn : inHandCard;

        // Release exactly ONE parked card to the current throw's seat, at most once per throw. Fired at the clip's
        // release event (via HideInHandCard), and as a safety-net for a missing clip/event so a card can't stall.
        private void ReleaseCurrent()
        {
            if (_released || _currentThrowSeat < 0) return;   // -1 sentinel = idle/awake; seat 0 (dealer) IS valid
            _released = true;
            if (tableView != null) tableView.ReleaseNextCard(_currentThrowSeat);
        }

        // --- Bind these to each throw clip's events in the Animancer inspector (public for that reason). ---
        // Deal beat: PICK (left hand from the machine) → PASS (left→right) → THROW (right releases). Events per clip:
        //   pickup  → ShowLeftCard      (card appears in the LEFT hand)
        //   pass    → HideLeftCard + ShowInHandCard   (left hands off; card appears in the RIGHT hand)
        //   throw   → HideInHandCard    (right lets go; the real card flies to the seat)
        public void ShowLeftCard()   { AnnouncePick(); if (inHandCardLeft != null) inHandCardLeft.SetActive(true); }
        public void HideLeftCard()   { if (inHandCardLeft != null) inHandCardLeft.SetActive(false); }
        public void ShowInHandCard() { AnnouncePick(); var h = RightHolder(); if (h != null) h.SetActive(true); }

        /// <summary>
        /// Card-out-of-the-shoe sound, fired from whichever event marks the card first appearing in her hands.
        ///
        /// Both entry points call this because the deal beat is authored two ways: a clip with a PICK event uses
        /// ShowLeftCard, a simpler one goes straight to ShowInHandCard. Guarded to ONE per throw so a clip binding
        /// both doesn't play it twice — <see cref="ThrowOne"/> re-arms it for the next card.
        /// </summary>
        private void AnnouncePick()
        {
            if (_pickAnnounced) return;
            _pickAnnounced = true;
            if (_audio == null) _audio = FindAnyObjectByType<TableAudio>(FindObjectsInactive.Include);
            if (_audio != null) _audio.PlayCardPickFromShoe();
        }

        private TableAudio _audio;
        private bool _pickAnnounced;

        /// <summary>
        /// The throw's RELEASE moment (bound to each throw clip's Throw event): hides the cosmetic in-hand prop AND
        /// flies exactly one real parked card to this throw's seat via <see cref="ReleaseCurrent"/>. The existing clips
        /// already bind THIS method to their release event, so no clip re-wiring is needed. Also called cosmetically
        /// from <see cref="Awake"/> / <see cref="PlayIdle"/>; the seat/released guards make those a no-op.
        /// </summary>
        public void HideInHandCard()
        {
            HideProp();
            ReleaseCurrent();
        }
    }
}
