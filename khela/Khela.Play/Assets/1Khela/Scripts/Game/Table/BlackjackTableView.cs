using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PlayCard.Game.Cards;
using PlayCard.Game.Dtos;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Renders a blackjack board from a server <see cref="BoardSnapshot"/>: the dealer's hand plus every
    /// occupied seat (split hands included). It DIFFS against the previous render — reusing unchanged cards
    /// and only renting / updating / recycling what actually changed — so repeated identical pushes (every
    /// SignalR tick + every poll) don't flicker. A face-down dealer hole card draws as a back because
    /// <see cref="CardVisual"/> honours IsCardUp (a reveal is just the same card with IsCardUp flipped).
    ///
    /// Cards lay out as a FAN (<see cref="CardLocalTRS"/>): each card steps over by Card Gap, tilts by Rotation
    /// Per Card around the anchor's up axis, and lifts to stack on top. <see cref="TableController"/> calls
    /// <see cref="Render"/> on every board (push or inline action) — the view does NOT subscribe to the hub itself.
    /// </summary>
    public sealed class BlackjackTableView : MonoBehaviour
    {
        [Header("Card")]
        [Tooltip("Prefab whose ROOT has a CardVisual (the wired Card_BJ).")]
        [SerializeField] private CardVisual cardPrefab;
        [Tooltip("Card art applied to every spawned card. Swap to reskin the whole table.")]
        [SerializeField] private CardSkin skin;

        [Header("Anchors")]
        [Tooltip("Where the dealer's hand is laid out.")]
        [SerializeField] private Transform dealerAnchor;
        [Tooltip("One anchor per seat. Element 0 = seat 1, element 1 = seat 2, and so on.")]
        [SerializeField] private Transform[] seatAnchors;

        [Header("Hand fan — DEFAULT (every seat + dealer use this unless overridden below)")]
        [Tooltip("GAP between adjacent cards (anchor-local). X = sideways spacing — SMALLER = more overlap; " +
                 "Y = forward/depth stagger. This is the card spacing.")]
        [SerializeField] private Vector2 cardGap = new Vector2(0.25f, 0f);
        [Tooltip("ROTATION: degrees each card tilts from the one before it — the fan splay. 0 = a straight, " +
                 "un-rotated overlapping stack.")]
        [SerializeField] private float rotationPerCard = 8f;
        [Tooltip("Per-card LIFT (anchor-local up) so each next card stacks clearly ON TOP of the previous — " +
                 "fixes draw order / z-fighting. Keep tiny (a few mm).")]
        [SerializeField] private float cardLift = 0.004f;
        [Tooltip("Which SIDE the fan opens toward (left ↔ right). The newest/last card is ALWAYS on top — this only " +
                 "chooses which side it sits on. Flip it if you rotated the anchor (e.g. Y 180°) and the fan opens the wrong way.")]
        [SerializeField] private bool mirrorFan = false;
        [Tooltip("Gap between split hands, in the anchor's local space. Use X (sideways) and/or Z (depth) to place " +
                 "the two hands side by side ON the felt — do NOT use Y (that's the felt normal = up/down, which " +
                 "stacks them top/bottom). Hands are centred, so 2 hands straddle the anchor by ±½ this.")]
        [SerializeField] private Vector3 splitHandStep = new Vector3(0.6f, 0f, 0f);

        [Header("Split-hand card tweak (optional)")]
        [Tooltip("When ON, cards in a SPLIT hand (2+ hands at the seat) use Split Card Gap instead of Card Gap AND " +
                 "shrink by Split Shrink — so the two fans read more compact. No effect on single hands.")]
        [SerializeField] private bool tweakSplitCards = false;
        [Tooltip("Card gap for split hands (replaces Card Gap while the tweak is on). X = sideways, Y = depth.")]
        [SerializeField] private Vector2 splitCardGap = new Vector2(0.18f, 0f);
        [Tooltip("How much smaller split cards are drawn — 0.1 = 10% smaller. Tweak on only.")]
        [Range(0f, 0.5f)] [SerializeField] private float splitShrink = 0.1f;

        [System.Serializable]
        public struct SeatFan
        {
            [Tooltip("Tick to give THIS seat its own gap/rotation (for its camera angle); untick to use the default.")]
            public bool overrideDefault;
            [Tooltip("X = sideways gap (smaller = more overlap), Y = forward/depth stagger.")]
            public Vector2 cardGap;
            [Tooltip("Degrees each card tilts from the previous.")]
            public float rotationPerCard;
            [Tooltip("Per-card up-lift for stacking order.")]
            public float cardLift;
            [Tooltip("Which side the fan opens toward for THIS seat (its anchor may be rotated differently). Last card stays on top.")]
            public bool mirrorFan;
        }

        [Header("Per-seat overrides (optional — for each seat's camera angle)")]
        [Tooltip("Element 0 = seat 1, element 1 = seat 2, … Tick a seat's Override Default to give it its own " +
                 "gap/rotation; otherwise it uses the default above. The dealer (seat 0) always uses the default.")]
        [SerializeField] private SeatFan[] seatFanOverrides;

        [Header("Deal / collect animation (optional — leave points empty to snap, no animation)")]
        [Tooltip("Cards SLIDE IN from here when dealt (the shoe / dealer's hand). The single point every dealt card " +
                 "flies from — the dealer animation later reuses this. Empty = cards just appear in place.")]
        [SerializeField] private Transform dealSource;
        [Tooltip("Cards SLIDE OUT to here when the round ends / a hand leaves (the discard tray). Empty = cards just clear.")]
        [SerializeField] private Transform discardTarget;
        [Tooltip("Seconds for a card to slide in from the deal source.")]
        [SerializeField] private float dealSeconds = 0.35f;
        [Tooltip("Seconds for the fan to re-settle when another card is added (the glide as it re-centres). 0 = snap.")]
        [SerializeField] private float recenterSeconds = 0.18f;
        [Tooltip("Seconds for a card to slide out to the discard at round end.")]
        [SerializeField] private float collectSeconds = 0.3f;
        [Tooltip("How much a collected card shrinks as it leaves (0.2 = 20% smaller). 0 = no shrink.")]
        [Range(0f, 1f)] [SerializeField] private float collectShrink = 0.2f;
        [Tooltip("Delay between each card being DEALT in — cards deal ONE BY ONE in real order (each hand one card, " +
                 "then the next; dealer last each round). 0 = all deal at once.")]
        [SerializeField] private float dealStagger = 0.12f;
        [Tooltip("Delay between each PLAYER/DEALER's cards being collected — a seat's cards leave TOGETHER, seats one " +
                 "after another. 0 = all collect at once.")]
        [SerializeField] private float collectStagger = 0.1f;
        [Tooltip("After the round's LAST card lands (e.g. a bust), hold this long before sweeping the felt — so the " +
                 "final hand is readable and the round doesn't clear the instant the server resolves. 0 = clear immediately.")]
        [SerializeField] private float roundEndHold = 0.7f;

        private CardPool _pool;
        private Vector3 _cardBaseScale = Vector3.one;   // the card prefab's localScale — split cards shrink from this

        private readonly Dictionary<int, Slot> _rendered = new Dictionary<int, Slot>();
        private readonly HashSet<int> _desired = new HashSet<int>();
        private readonly List<int> _stale = new List<int>();
        private readonly HashSet<int> _pendingDeal = new HashSet<int>();    // new cards parked at the shoe, awaiting their deal (timer or throw)
        private readonly List<NewCard> _newThisPass = new List<NewCard>();  // new cards seen this Render, to schedule in deal order

        // Throw-gated dealing: while a conductor (the dealer) is registered, each parked card waits in its SEAT's FIFO
        // and flies only when the conductor calls ReleaseNextCard(seat) at a throw's release. With no conductor these
        // stay empty and the view self-deals on its own stagger (fallback).
        private readonly Dictionary<int, Queue<NewCard>> _releaseQueues = new Dictionary<int, Queue<NewCard>>();
        private MonoBehaviour _conductor;                       // the dealer, when present (held as MonoBehaviour → no hard dependency on it)
        private bool ConductorActive => _conductor != null;     // Unity-null aware: a destroyed/disabled dealer falls back automatically
        private bool _collectPending;                           // a round-end sweep is held until the last card LANDS (so a bust/final card isn't cleared mid-flight)
        private float _collectReadyAt = -1f;                    // unscaled time the held sweep may run (after roundEndHold); -1 = not armed

        private struct Slot { public CardVisual Card; public CardView Data; }
        private struct NewCard { public int Key; public int Seat; public CardMover Mover; public Vector3 Pos; public Quaternion Rot; public Vector3 Scale; public int Order; }

        private void Awake()
        {
            _pool = new CardPool(cardPrefab, transform);
            if (cardPrefab != null) _cardBaseScale = cardPrefab.transform.localScale;
        }

        /// <summary>
        /// Anchor-local transform (position + rotation) for card <paramref name="cardIndex"/> of a
        /// <paramref name="cardCount"/>-card hand, where this is hand <paramref name="handIndex"/> of
        /// <paramref name="handCount"/> at <paramref name="seat"/> (1-based; 0 = dealer). The hand is centred on its
        /// slot (<see cref="HandCenterLocal"/>) — so a split's two hands straddle the seat anchor with a clear gap —
        /// and the cards fan about that point: each steps sideways by <c>cardGap</c>, tilts by <c>rotationPerCard</c>
        /// around the anchor up axis, and lifts by <c>cardLift</c> so the newest sits on top. Shared by the runtime
        /// layout AND the anchor preview, so the editor shows exactly what gets dealt.
        /// </summary>
        public void CardLocalTRS(int seat, int handIndex, int handCount, int cardIndex, int cardCount,
                                 out Vector3 pos, out Quaternion rot, out float scale)
        {
            ResolveFan(seat, out Vector2 gap, out float anglePer, out float lift, out bool mirror);

            // Split-hand tweak: only when the toggle is on AND the seat is actually split — use the alternate gap
            // and shrink the cards (scale is a uniform multiplier the caller applies to the card's base scale).
            bool tweak = tweakSplitCards && handCount > 1;
            if (tweak) gap = splitCardGap;
            scale = tweak ? Mathf.Max(0.01f, 1f - splitShrink) : 1f;

            float k = cardIndex - (cardCount - 1) * 0.5f;       // centred index: −left … 0 middle … +right
            float s = (mirror ? -1f : 1f) * k;                  // signed index — `mirror` flips which side the fan opens to
            rot = Quaternion.Euler(0f, s * anglePer, 0f);       // tilt follows the open side
            // LIFT is ALWAYS by deal order (cardIndex), so the newest/last card is always on top — mirror never changes that.
            Vector3 offset = new Vector3(s * gap.x, lift * cardIndex, s * gap.y);
            pos = HandCenterLocal(handIndex, handCount) + offset;
        }

        /// <summary>
        /// Anchor-local CENTRE of hand <paramref name="handIndex"/> when a seat plays <paramref name="handCount"/>
        /// hands (a split). Hands are centred about the seat anchor and gapped by <c>splitHandStep</c>: 1 hand sits
        /// at the anchor, 2 split hands straddle it symmetrically (a clear gap between them). The card fan, the
        /// active-hand glow, and the per-hand bet stacks all build around this point.
        /// </summary>
        public Vector3 HandCenterLocal(int handIndex, int handCount)
            => splitHandStep * (handIndex - (handCount - 1) * 0.5f);

        /// <summary>The world anchor for a seat (1-based) or the dealer (0). Null if not authored.</summary>
        public Transform SeatAnchor(int seat) => seat <= 0 ? dealerAnchor : AnchorForSeat(seat);

        /// <summary>
        /// True when the card at (seat, handIndex, cardIndex) is DEALT AND LANDED — created, no longer parked in the
        /// shoe/hand, and not mid-flight. Board-derived UI (the value / Blackjack / bust badge) gates on this so score
        /// reveals in step with the ANIMATED deal instead of the instant the server pushes the final hand. Cards deal
        /// in order, so a hand's last card being settled means the whole hand is on the felt.
        /// </summary>
        public bool CardSettled(int seat, int handIndex, int cardIndex)
        {
            int key = SlotKey(seat, handIndex, cardIndex);
            if (_pendingDeal.Contains(key)) return false;                                     // still parked (awaiting throw/stagger)
            if (!_rendered.TryGetValue(key, out var slot) || slot.Card == null) return false; // not created yet
            var mover = slot.Card.GetComponent<CardMover>();
            return mover == null || !mover.Animating;                                         // landed (not gliding in)
        }

        /// <summary>
        /// True while ANY card is still coming in — parked at the shoe/hand awaiting its throw, or gliding to its spot.
        /// Turn-driven UI (action buttons, the decision-pose/FOV camera) holds on this so it activates only once the
        /// ANIMATED deal has fully landed, not the instant the server pushes the resolved board.
        /// </summary>
        public bool AnyCardAnimating()
        {
            if (_pendingDeal.Count > 0) return true;                        // parked, awaiting a throw/stagger
            foreach (var kv in _rendered)
            {
                var card = kv.Value.Card;
                if (card == null) continue;
                var mover = card.GetComponent<CardMover>();
                if (mover != null && mover.Animating) return true;         // gliding in
            }
            return false;
        }

        // Per-seat fan params: a seat's override if ticked, else the shared default. seat 0 (dealer) = default.
        private void ResolveFan(int seat, out Vector2 gap, out float anglePer, out float lift, out bool mirror)
        {
            gap = cardGap; anglePer = rotationPerCard; lift = cardLift; mirror = mirrorFan;
            int idx = seat - 1;
            if (seatFanOverrides != null && idx >= 0 && idx < seatFanOverrides.Length && seatFanOverrides[idx].overrideDefault)
            {
                var o = seatFanOverrides[idx];
                gap = o.cardGap; anglePer = o.rotationPerCard; lift = o.cardLift; mirror = o.mirrorFan;
            }
        }

        /// <summary>Lay out a board snapshot, diffing against the last render. Safe to call on every push.</summary>
        public void Render(BoardSnapshot board)
        {
            if (board == null || cardPrefab == null || _pool == null) return;

            _desired.Clear();

            // Cards only while a round is LIVE. Between rounds — and on entering a table whose last (settled) round
            // still lingers in the board state — nothing is desired, so the stale-release below sweeps the felt
            // clean (no leftover dealer/player hands during betting). The result line still reports the outcome.
            if (board.RoundInProgress)
            {
                if (board.Dealer != null && dealerAnchor != null)
                    LayOutHand(board.Dealer.Cards, dealerAnchor, 0, 0, 1);

                if (board.Seats != null)
                {
                    foreach (var seat in board.Seats)
                    {
                        if (seat?.Player == null) continue;
                        var anchor = AnchorForSeat(seat.SeatNumber);
                        if (anchor == null) continue;   // server seat beyond our authored anchors (e.g. 4–5) — skip

                        var hands = seat.Player.Hands;
                        for (int h = 0; h < hands.Count; h++)
                            LayOutHand(hands[h].Cards, anchor, seat.SeatNumber, h, hands.Count);
                    }
                }
            }

            // Deal this pass's new cards in ONE BY ONE, in real dealing order. With a conductor present each card waits
            // in its seat's release queue for the dealer's throw; otherwise the view self-deals on its own stagger.
            if (_newThisPass.Count > 0)
            {
                _newThisPass.Sort((a, b) => a.Order.CompareTo(b.Order));
                if (ConductorActive)
                    for (int i = 0; i < _newThisPass.Count; i++) EnqueueForRelease(_newThisPass[i]);
                else
                    for (int i = 0; i < _newThisPass.Count; i++)
                        StartCoroutine(DealRoutine(_newThisPass[i], i * Mathf.Max(0f, dealStagger)));
                _newThisPass.Clear();
            }

            // Anything no longer on the board leaves — collected per seat (a seat's cards together), seats staggered.
            // BUT hold the sweep while a card is still coming in (e.g. a just-dealt BUST/final card), so the round
            // doesn't clear the felt before that card lands. Update() finishes the deferred sweep once it settles.
            _stale.Clear();
            foreach (var kv in _rendered)
                if (!_desired.Contains(kv.Key)) _stale.Add(kv.Key);
            // Defer the sweep to Update, which waits for the last card to LAND then holds — so a bust/final card isn't
            // cleared mid-flight, and a re-push of the settled board can't skip the hold. (Collected cards leave
            // _rendered, so they won't be re-swept.)
            if (_stale.Count > 0) _collectPending = true;
            else { _collectPending = false; _collectReadyAt = -1f; }
        }

        private void Update()
        {
            // Complete a deferred round-end sweep once the last card has LANDED, then hold roundEndHold so the final
            // hand (e.g. a bust) is readable. Render is board-driven, but a bust/final card lands BETWEEN pushes —
            // without this the felt would clear the instant the server resolves.
            if (!_collectPending) { _collectReadyAt = -1f; return; }
            if (AnyCardAnimating()) { _collectReadyAt = -1f; return; }              // still landing → keep waiting
            if (_collectReadyAt < 0f) { _collectReadyAt = Time.unscaledTime + Mathf.Max(0f, roundEndHold); return; }
            if (Time.unscaledTime < _collectReadyAt) return;                        // hold so the final hand is visible

            _collectPending = false; _collectReadyAt = -1f;
            _stale.Clear();
            foreach (var kv in _rendered)
                if (!_desired.Contains(kv.Key)) _stale.Add(kv.Key);
            if (_stale.Count > 0) CollectStale();
        }

        private static CardMover EnsureMover(CardVisual card)
        {
            var m = card.GetComponent<CardMover>();
            return m != null ? m : card.gameObject.AddComponent<CardMover>();
        }

        // Real dealing order for a card: round by round (cardIndex), players (seat asc) then the dealer (seat 0) LAST.
        private static int DealOrder(int seat, int cardIndex) => cardIndex * 1000 + (seat == 0 ? 999 : seat);

        // Reveal ONE parked card: drop it from the pending set, activate it, and slide it from the shoe to its fan
        // spot. Idempotent — guarded on _pendingDeal so a double release (fast push / stale queue entry) is a no-op.
        // Shared by the conductor's ReleaseNextCard and the no-conductor DealRoutine fallback.
        private void Release(NewCard nc)
        {
            if (nc.Mover == null || !_pendingDeal.Contains(nc.Key)) return;   // collected, or already released
            _pendingDeal.Remove(nc.Key);
            nc.Mover.gameObject.SetActive(true);
            nc.Mover.Target(nc.Pos, nc.Rot, nc.Scale, dealSeconds);
        }

        // FALLBACK (no conductor): deal one queued card in on its turn — wait its stagger, then release it.
        private IEnumerator DealRoutine(NewCard nc, float delay)
        {
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
            Release(nc);
        }

        // Park a card in its SEAT's release FIFO (conductor path). Cards arrive here already sorted by deal order, so a
        // seat's queue holds its cards — spanning EVERY split hand at that seat — in the order the dealer throws them.
        private void EnqueueForRelease(NewCard nc)
        {
            if (!_releaseQueues.TryGetValue(nc.Seat, out var q)) { q = new Queue<NewCard>(); _releaseQueues[nc.Seat] = q; }
            q.Enqueue(nc);
        }

        /// <summary>
        /// Fly the EARLIEST-dealt still-parked card for <paramref name="seat"/> from the dealer's hand to its fan spot.
        /// The deal conductor (<see cref="DealerAnimator"/>) calls this at each throw's RELEASE moment, so exactly one
        /// card leaves her hand per throw. No-op when nothing is parked for the seat (a board push that outran the
        /// throws, a seat with no throw clip, or a double release) so a card is never lost or dealt twice. Ignored
        /// entirely when no conductor is registered (the view self-deals instead).
        /// </summary>
        public void ReleaseNextCard(int seat)
        {
            if (!ConductorActive) return;
            if (!_releaseQueues.TryGetValue(seat, out var q)) return;
            while (q.Count > 0)
            {
                var nc = q.Dequeue();
                if (_pendingDeal.Contains(nc.Key)) { Release(nc); return; }   // still parked → fly exactly this one
                // otherwise it was collected / already released — discard and try the seat's next parked card
            }
        }

        // Rebuild each release FIFO keeping only cards still parked (in _pendingDeal), preserving order. Runs after
        // CollectStale has removed collected keys from _pendingDeal, so a card abandoned mid-deal can't strand a stale
        // (pooled) entry that a later round's identical SlotKey would wrongly release.
        private void PurgeReleaseQueues()
        {
            if (_releaseQueues.Count == 0) return;
            foreach (var q in _releaseQueues.Values)
            {
                int n = q.Count;
                for (int i = 0; i < n; i++)
                {
                    var nc = q.Dequeue();
                    if (_pendingDeal.Contains(nc.Key)) q.Enqueue(nc);
                }
            }
        }

        // ---- Deal conductor registration (optional; no hard dependency on the dealer) ----

        /// <summary>
        /// Register the deal conductor (the dealer). While one is registered, newly dealt cards PARK at the shoe and
        /// fly only when the conductor calls <see cref="ReleaseNextCard"/> at a throw's release — so cards visibly
        /// leave her hand as she throws. Idempotent; a later conductor replaces an earlier one.
        /// </summary>
        public void RegisterConductor(MonoBehaviour conductor)
        {
            if (conductor != null) _conductor = conductor;
        }

        /// <summary>
        /// Clear the deal conductor (dealer disabled / destroyed). The view reverts to its own staggered self-deal, and
        /// anything still parked is released at once so a vanishing dealer can never strand a card. Only clears if
        /// <paramref name="conductor"/> is the one currently registered.
        /// </summary>
        public void UnregisterConductor(MonoBehaviour conductor)
        {
            if (_conductor != conductor) return;
            _conductor = null;
            FlushReleaseQueues();
        }

        // Release everything still parked immediately (no throw left to wait for) — used when the conductor goes away.
        private void FlushReleaseQueues()
        {
            foreach (var q in _releaseQueues.Values)
                while (q.Count > 0) Release(q.Dequeue());
            _releaseQueues.Clear();
        }

        /// <summary>
        /// Show every currently-PARKED card INSTANTLY in its fan spot (SNAP, no deal-in, no throw). The conductor calls
        /// this on its baseline board so a mid-round JOIN reveals the already-dealt cards at once, instead of leaving
        /// them parked hidden waiting on throws that (correctly) never fire for cards dealt before we arrived.
        /// </summary>
        public void SnapParkedCards()
        {
            foreach (var q in _releaseQueues.Values)
                while (q.Count > 0)
                {
                    var nc = q.Dequeue();
                    if (nc.Mover == null || !_pendingDeal.Contains(nc.Key)) continue;
                    _pendingDeal.Remove(nc.Key);
                    nc.Mover.gameObject.SetActive(true);
                    nc.Mover.Snap(nc.Pos, nc.Rot, nc.Scale);
                }
            _releaseQueues.Clear();
        }

        // Cards left the board: group by seat so a hand's cards leave TOGETHER, and stagger the seats (dealer last).
        private void CollectStale()
        {
            var groups = new Dictionary<int, List<CardVisual>>();
            for (int i = 0; i < _stale.Count; i++)
            {
                int key = _stale[i];
                var card = _rendered[key].Card;
                _rendered.Remove(key);
                _pendingDeal.Remove(key);
                int seat = key / 10000;   // SlotKey packs seat*10000 + hand*100 + cardIndex
                if (!groups.TryGetValue(seat, out var list)) { list = new List<CardVisual>(); groups[seat] = list; }
                list.Add(card);
            }

            PurgeReleaseQueues();   // drop collected cards from the release FIFOs so a reused SlotKey can't fly a pooled card

            if (discardTarget == null || collectSeconds <= 0f)
            {
                foreach (var g in groups.Values) foreach (var c in g) _pool.Release(c);
                return;
            }

            var seats = new List<int>(groups.Keys);
            seats.Sort((a, b) => (a == 0 ? 999 : a).CompareTo(b == 0 ? 999 : b));   // players first, dealer last
            for (int gi = 0; gi < seats.Count; gi++)
            {
                float delay = gi * Mathf.Max(0f, collectStagger);
                foreach (var c in groups[seats[gi]]) StartCoroutine(CollectRoutine(c, delay));
            }
        }

        private IEnumerator CollectRoutine(CardVisual card, float delay)
        {
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
            if (card == null) yield break;
            if (!card.gameObject.activeSelf) { _pool.Release(card); yield break; }   // a hidden, undealt card — just reclaim
            var tr = card.transform;
            var parent = tr.parent;   // still its anchor — convert the world discard point into that local frame
            Vector3 local = parent != null ? parent.InverseTransformPoint(discardTarget.position) : tr.localPosition;
            EnsureMover(card).Target(local, tr.localRotation, tr.localScale * (1f - collectShrink), collectSeconds);
            yield return new WaitForSecondsRealtime(collectSeconds);
            _pool.Release(card);
        }

        private void LayOutHand(List<CardView> cards, Transform anchor, int seat, int handIndex, int handCount)
        {
            if (cards == null) return;
            int count = cards.Count;

            for (int i = 0; i < count; i++)
            {
                int key = SlotKey(seat, handIndex, i);
                _desired.Add(key);
                var data = cards[i];
                CardLocalTRS(seat, handIndex, handCount, i, count, out var pos, out var rot, out var scale);

                var targetScale = _cardBaseScale * scale;
                if (_rendered.TryGetValue(key, out var slot))
                {
                    if (_pendingDeal.Contains(key))
                    {
                        // Still parked at the shoe waiting its turn — don't move it; just keep its face current.
                        if (!SameCard(slot.Data, data)) { slot.Card.SetCard(data); slot.Data = data; _rendered[key] = slot; }
                    }
                    else
                    {
                        // Adding a card re-centres the fan → glide existing cards to the new pose (idempotent, so an
                        // identical push doesn't restart it; an in-flight deal-in keeps its own target).
                        EnsureMover(slot.Card).Target(pos, rot, targetScale, recenterSeconds);
                        if (!SameCard(slot.Data, data)) { slot.Card.SetCard(data); slot.Data = data; _rendered[key] = slot; }
                    }
                }
                else
                {
                    var card = _pool.Rent(anchor);
                    if (skin != null) card.Skin = skin;
                    card.SetCard(data);
                    var mover = EnsureMover(card);
                    _rendered[key] = new Slot { Card = card, Data = data };

                    if (dealSource != null && dealSeconds > 0f && (ConductorActive || dealStagger > 0f))
                    {
                        // Sequential deal: park HIDDEN at the shoe. Render then either hands it to the dealer's
                        // per-seat release queue (conductor present → flies on her throw) or slides it in on the
                        // view's own stagger (fallback) — both drain _newThisPass in real deal order. A conductor drives
                        // the timing, so we park regardless of dealStagger (that value is only the fallback's cadence).
                        mover.Snap(anchor.InverseTransformPoint(dealSource.position), rot, targetScale);
                        card.gameObject.SetActive(false);
                        _pendingDeal.Add(key);
                        _newThisPass.Add(new NewCard { Key = key, Seat = seat, Mover = mover, Pos = pos, Rot = rot, Scale = targetScale, Order = DealOrder(seat, i) });
                    }
                    else if (dealSource != null && dealSeconds > 0f)
                    {
                        // Animated but not staggered → all slide in from the shoe together.
                        mover.Snap(anchor.InverseTransformPoint(dealSource.position), rot, targetScale);
                        mover.Target(pos, rot, targetScale, dealSeconds);
                    }
                    else
                    {
                        mover.Snap(pos, rot, targetScale);   // no deal animation → appear in place
                    }
                }
            }
        }

        private Transform AnchorForSeat(int seatNumber)
        {
            int idx = seatNumber - 1;
            if (seatAnchors == null || idx < 0 || idx >= seatAnchors.Length) return null;
            return seatAnchors[idx];
        }

        // dealer = seat 0; pack (seat, hand, cardIndex) into one int (hands + cards each stay well under 100).
        private static int SlotKey(int seat, int hand, int cardIndex) => (seat * 100 + hand) * 100 + cardIndex;

        private static bool SameCard(CardView a, CardView b)
            => a != null && b != null && a.FaceVal == b.FaceVal && a.Suit == b.Suit && a.IsCardUp == b.IsCardUp;

        /// <summary>
        /// Instantiate ONE real card (the actual prefab + skin) for the editor anchor preview, so the Scene view
        /// shows a true card — real size, real art — not a guessed rectangle. <paramref name="sampleIndex"/> just
        /// picks a sample face so the cards look distinct. Editor-tooling only; the previewer flags these DontSave
        /// and cleans them up.
        /// </summary>
        /// <summary>The card prefab + skin the preview should build with — lets the editor previewer notice a
        /// swap and rebuild. Editor-tooling accessors.</summary>
        public CardVisual PreviewPrefab => cardPrefab;
        public CardSkin PreviewSkin => skin;

        public CardVisual InstantiatePreviewCard(Transform parent, int sampleIndex)
        {
            if (cardPrefab == null) return null;
            var c = Instantiate(cardPrefab);
            c.transform.SetParent(parent, false);   // keep the prefab's localScale, exactly like CardPool.Rent —
                                                    // so the preview size matches the dealt card even if the anchor is scaled
            if (skin != null) c.Skin = skin;
            c.SetCard(new CardId((CardRank)((sampleIndex % 13) + 2), (CardSuit)(sampleIndex % 4), true));
            return c;
        }
    }
}
