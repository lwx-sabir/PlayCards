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
    ///
    /// A SPLIT seat gets a second layout stage. Two (or more) hands each growing to 4–5 cards will not fit in one
    /// seat's width — the outer seats' far hand walks off the felt, and neighbouring seats' hands and badges collide.
    /// So a hand that has FINISHED playing is TUCKED (see the Tuck settings): shrunk, its gap tightened, optionally
    /// nudged aside, leaving the seat's width to the hand still being played. The tuck lives in
    /// <see cref="CardLocalTRS"/>, so the value and result badges — which read the same geometry — follow it for free,
    /// and the bet stacks (which read <see cref="HandCenterLocal"/>) deliberately do NOT move: the wager stays where it
    /// was placed and where the round-end director pays it.
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

        [Header("Played split hand — TUCK AWAY (a finished hand shrinks so the live one has room)")]
        [Tooltip("When a SPLIT hand has finished playing (stood / busted / doubled / split aces), tuck it away: its " +
                 "cards shrink, their gap tightens and the whole hand shifts by Tuck Nudge — so the hand still being " +
                 "played keeps the seat's width to itself. Without it the right seat's right-hand cards run off the " +
                 "table past 2 cards, and two seats' hands (and their badges) collide. No effect on a single hand.")]
        [SerializeField] private bool tuckPlayedHands = true;
        [Tooltip("How much smaller a FINISHED split hand is drawn — 0.3 = 30% smaller. Multiplies with Split Shrink.")]
        [Range(0f, 0.8f)] [SerializeField] private float tuckShrink = 0.3f;
        [Tooltip("Card gap of a finished hand, as a FRACTION of the gap it would otherwise use — 0.55 = 45% tighter. " +
                 "A fraction, not an absolute, so per-seat gap overrides still apply.")]
        [Range(0.1f, 1f)] [SerializeField] private float tuckGapScale = 0.55f;
        [Tooltip("Per-card LIFT on a finished hand, as a FRACTION of Card Lift. Card Lift is an ABSOLUTE distance and " +
                 "does not shrink with the cards, so across a 5-card hand it stacks up to a visible staircase — this " +
                 "flattens it. ⚠ DO NOT USE 0. Lift is the only thing separating overlapping cards in depth; at 0 they " +
                 "are coplanar and the GPU z-fights, which is the striped, shredded-looking overlap. 0.15 is ~0.6mm a " +
                 "card — flat to the eye, and far more separation than the depth buffer needs.")]
        [Range(0.02f, 1f)] [SerializeField] private float tuckLiftScale = 0.15f;
        [Tooltip("How far a finished hand slides back TOWARD the seat centre, closing the gap between the two hands — " +
                 "0 = stays where it was dealt, 1 = sits right on the seat anchor. Split Hand Step has to be wide " +
                 "enough for two FULL-SIZE hands, so once one of them shrinks that gap is far too big; this takes it " +
                 "back. Only the TUCKED hand moves — the one still being played never shifts under the player.")]
        [Range(0f, 1f)] [SerializeField] private float tuckPullIn = 0.35f;
        [Tooltip("Extra anchor-local shift for a finished hand, applied after Tuck Pull In (same axes as Split Hand " +
                 "Step: X sideways, Z depth, leave Y at 0). For asymmetric fine-tuning the pull-in can't express.")]
        [SerializeField] private Vector3 tuckNudge = Vector3.zero;
        [Tooltip("Seconds for a hand to glide + shrink into its tucked pose. Separate from Recenter Seconds — that is " +
                 "tuned for the small shuffle when a card is added, and this is a much bigger move.")]
        [SerializeField] private float tuckSeconds = 0.28f;
        [Tooltip("Pause after a hand's LAST CARD LANDS before it tucks away, so the player reads the card that ended " +
                 "the hand and its badge before it shrinks. The server marks a hand finished the instant it resolves — " +
                 "a bust is 'done' while the busting card is still in the air — so without this the hand is yanked " +
                 "away before you have seen what happened.")]
        [SerializeField] private float tuckDelaySeconds = 0.6f;

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

        [Header("Sweep flourish — cards lift, turn face-down, then leave")]
        [Tooltip("ON: a face-up card lifts off the felt, turns onto its back, and only then sweeps away — the gesture " +
                 "a dealer actually makes. OFF: it slides away still showing its face, which is the old behaviour.")]
        [SerializeField] private bool sweepTurnsFaceDown = true;

        [Tooltip("How far the card rises off the felt before turning, in local units. It needs enough clearance to " +
                 "turn without cutting through the cards under it.")]
        [SerializeField] private float sweepLift = 0.03f;

        [Tooltip("Seconds for the lift.")]
        [SerializeField] private float sweepLiftSeconds = 0.14f;

        [Tooltip("How far the lift springs PAST its target before settling back, as a fraction (0.35 = 35% over). " +
                 "This is the elasticity in the pull: a card that eases straight to its height reads as being " +
                 "positioned, one that overshoots and settles reads as being picked up.")]
        [SerializeField, Range(0f, 1f)] private float sweepLiftOvershoot = 0.35f;

        [Tooltip("Seconds for the lift to settle back from its overshoot. Shorter than the lift itself — the spring " +
                 "back is always quicker than the throw out.")]
        [SerializeField] private float sweepSettleSeconds = 0.09f;

        [Tooltip("Seconds for the turn onto its back. The art swaps at the apex, while the card is edge-on.")]
        [SerializeField] private float sweepFlipSeconds = 0.22f;

        [Tooltip("Elasticity of the TURN itself — lift and scale-pop through the apex, and Overshoot, which is the " +
                 "ease-out-back spring as it lands flat. Raise Overshoot past 1.7 for a springier turn. Independent " +
                 "of the dealer's reveal juice, so making the sweep bouncier can't affect the hole-card reveal.")]
        [SerializeField] private CardFlipTuning sweepFlipJuice = new CardFlipTuning();

        [Tooltip("Local euler the card turns through to go edge-on — pick the axis that turns its face away. Same " +
                 "meaning as the dealer reveal's flip axis.")]
        [SerializeField] private Vector3 sweepFlipEdgeEuler = new Vector3(0f, 0f, 90f);
        [Tooltip("After the round's LAST card lands (e.g. a bust), hold this long before sweeping the felt — so the " +
                 "final hand is readable and the round doesn't clear the instant the server resolves. 0 = clear immediately.")]
        [SerializeField] private float roundEndHold = 0.7f;

        private CardPool _pool;
        private Vector3 _cardBaseScale = Vector3.one;   // the card prefab's localScale — split cards shrink from this

        private readonly Dictionary<int, Slot> _rendered = new Dictionary<int, Slot>();
        private readonly HashSet<int> _desired = new HashSet<int>();
        private readonly List<int> _stale = new List<int>();
        private readonly HashSet<int> _pendingDeal = new HashSet<int>();    // new cards parked at the shoe, awaiting their deal (timer or throw)
        private readonly HashSet<int> _landed = new HashSet<int>();         // cards that have COMPLETED their deal-in — a later re-layout glide must not un-land them
        private readonly HashSet<int> _tucked = new HashSet<int>();         // (seat,hand) of finished split hands, drawn compacted; refreshed only when Render re-lays the felt
        private readonly List<NewCard> _newThisPass = new List<NewCard>();  // new cards seen this Render, to schedule in deal order
        private readonly Dictionary<int, CardView> _desiredData = new Dictionary<int, CardView>();  // key -> data this pass, for split re-key
        private readonly List<int> _movableKeys = new List<int>();          // rendered cards a split may migrate (scratch)

        // Throw-gated dealing: while a conductor (the dealer) is registered, each parked card waits in its SEAT's FIFO
        // and flies only when the conductor calls ReleaseNextCard(seat) at a throw's release. With no conductor these
        // stay empty and the view self-deals on its own stagger (fallback).
        private readonly Dictionary<int, Queue<NewCard>> _releaseQueues = new Dictionary<int, Queue<NewCard>>();
        private MonoBehaviour _conductor;                       // the dealer, when present (held as MonoBehaviour → no hard dependency on it)
        private bool ConductorActive => _conductor != null;     // Unity-null aware: a destroyed/disabled dealer falls back automatically
        private bool _collectPending;                           // a round-end sweep is held until the last card LANDS (so a bust/final card isn't cleared mid-flight)
        private float _collectReadyAt = -1f;                    // unscaled time the held sweep may run (after roundEndHold); -1 = not armed
        private bool _roundEndHeld;                             // a RoundEndDirector owns the felt: Render is frozen + the deferred sweep cancelled until it calls EndRoundEnd
        private bool _peekHeld;                                 // the dealer is peeking at her hole card → decisions (and the turn clock) wait

        private struct Slot { public CardVisual Card; public CardView Data; public CardMover Mover; }
        private struct NewCard { public int Key; public int Seat; public CardMover Mover; public Vector3 Pos; public Quaternion Rot; public Vector3 Scale; public int Order; }

        private void Awake()
        {
            _pool = new CardPool(cardPrefab, transform);
            if (cardPrefab != null) _cardBaseScale = cardPrefab.transform.localScale;
            // Enter Play Mode without a domain reload and this field would still hold whatever the anchor gizmo last
            // previewed, permanently tucking a hand in a live round. It is edit-time tooling only — start it off.
            _previewTuckMask = -1;
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

            bool split = handCount > 1;

            // Split-hand tweak: only when the toggle is on AND the seat is actually split — use the alternate gap
            // and shrink the cards (scale is a uniform multiplier the caller applies to the card's base scale).
            bool tweak = tweakSplitCards && split;
            if (tweak) gap = splitCardGap;
            scale = tweak ? Mathf.Max(0.01f, 1f - splitShrink) : 1f;

            // TUCK: a split hand that has FINISHED playing gets out of the way of the one still being played. It is the
            // only reason a seat ever runs out of room — a hand grows to 3, 4, 5 cards while its neighbour is doing the
            // same, and on the outer seats the far hand walks off the felt entirely. Shrinking + tightening the gap of
            // whatever is already resolved keeps every remaining hand readable no matter how many times the seat splits.
            if (split && IsTucked(seat, handIndex))
            {
                scale *= Mathf.Max(0.05f, 1f - tuckShrink);
                gap *= Mathf.Clamp01(tuckGapScale);
                lift *= Mathf.Clamp01(tuckLiftScale);
            }
            Vector3 centre = HandCenterDrawn(seat, handIndex, handCount);

            float k = cardIndex - (cardCount - 1) * 0.5f;       // centred index: −left … 0 middle … +right
            float s = (mirror ? -1f : 1f) * k;                  // signed index — `mirror` flips which side the fan opens to
            rot = Quaternion.Euler(0f, s * anglePer, 0f);       // tilt follows the open side
            // LIFT is ALWAYS by deal order (cardIndex), so the newest/last card is always on top — mirror never changes that.
            Vector3 offset = new Vector3(s * gap.x, lift * cardIndex, s * gap.y);
            pos = centre + offset;
        }

        // (seat, hand) identity for the tuck set — the dealer is seat 0 and never splits, so it never appears here.
        private static int HandKey(int seat, int handIndex) => seat * 100 + handIndex;

        /// <summary>
        /// Take a hand's cards off the felt NOW, mid-round — a busted hand the dealer has already collected. They are
        /// dropped from the desired set and swept to the discard by the normal stale path, and stay gone for the rest
        /// of the round even though the server keeps listing them.
        ///
        /// Cleared automatically when the next round starts, so nothing has to remember to undo it.
        /// </summary>
        public void ClearHand(int seat, int handIndex)
        {
            int key = HandKey(seat, handIndex);
            _clearingHands.Remove(key);                                 // the removal is DONE, not pending
            if (!_clearedHands.Add(key)) return;                        // already gone

            // Sweep this hand's cards DIRECTLY and unconditionally. Routing it through a re-render — "drop the hand
            // from the desired set and let the stale path take it" — has two ways to silently do nothing, and the last
            // hand of a split busting can land in either because the round settles while this is still running:
            //
            //   • Render is a NO-OP while the round-end director holds the felt, so the call just returns; and
            //   • the render path that DOES run only ARMS a deferred sweep (_collectPending), which BeginRoundEnd
            //     then explicitly cancels — so the sweep is queued and thrown away a frame later.
            //
            // Both end the same way: the chips fly off a hand whose cards stay on the felt for the rest of the round.
            // The destination is identical either way (CollectStale is the one sweep every path funnels through) —
            // this just reaches it without going through a gate that may be closed or a queue that may be discarded.
            SweepHand(seat, handIndex);

            // The re-render is still wanted for what's LEFT: the surviving hand re-fans and re-tucks now that its
            // neighbour is gone. Skipped while held — the director owns the felt, and it re-lays it out on release.
            if (!_roundEndHeld && _lastBoard != null) Render(_lastBoard);
        }

        /// <summary>Send exactly one hand's rendered cards to the discard, without a re-render.</summary>
        private void SweepHand(int seat, int handIndex)
        {
            _stale.Clear();
            foreach (var kv in _rendered)
            {
                // SlotKey packs seat * 10000 + hand * 100 + cardIndex.
                if (kv.Key / 10000 != seat) continue;
                if ((kv.Key / 100) % 100 != handIndex) continue;
                _stale.Add(kv.Key);
            }
            if (_stale.Count > 0) CollectStale();   // fully collected here, so the director's later sweep skips them
        }

        /// <summary>True once <see cref="ClearHand"/> has taken this hand off the felt this round.</summary>
        public bool IsHandCleared(int seat, int handIndex) => _clearedHands.Contains(HandKey(seat, handIndex));

        /// <summary>
        /// This hand is about to be taken off the felt (a bust the dealer is collecting), so it must NOT tuck.
        ///
        /// The tuck exists to free room for a hand that is still being played; a hand that is leaving in a moment
        /// doesn't need room, and shrinking it first meant a busted split hand played two contradictory gestures —
        /// tuck away, then get collected and swept. Un-tucks it again if it already had.
        /// </summary>
        public void MarkHandClearing(int seat, int handIndex) => _clearingHands.Add(HandKey(seat, handIndex));

        /// <summary>
        /// Is a hand at this seat currently being taken off the felt? True from <see cref="MarkHandClearing"/> until
        /// <see cref="ClearHand"/> — i.e. for the whole read-the-bust → collect → sweep sequence.
        ///
        /// The next hand's controls gate on this. Busting one half of a split hands the turn straight to the other
        /// half, and the server is right to do that — but the dealer is still taking the busted hand's chips, so the
        /// player was being asked to act on hand two while hand one was mid-collection. Two things happening to the
        /// same seat at once, and the one you are meant to be watching is the one you are being pulled away from.
        /// </summary>
        public bool HandClearingAtSeat(int seat)
        {
            foreach (int key in _clearingHands) if (key / 100 == seat) return true;   // HandKey packs seat * 100 + hand
            return false;
        }

        // Hands removed mid-round, and hands about to be. Keyed like the tuck set; emptied when a new round begins.
        private readonly HashSet<int> _clearedHands = new HashSet<int>();
        private readonly HashSet<int> _clearingHands = new HashSet<int>();

        /// <summary>Is this hand drawn TUCKED (finished, compacted)? Honours the editor preview override.</summary>
        private bool IsTucked(int seat, int handIndex)
        {
            if (!tuckPlayedHands) return false;
            if (_previewTuckMask >= 0) return (_previewTuckMask & (1 << handIndex)) != 0;   // editor tooling — see SetPreviewTuck
            return _tucked.Contains(HandKey(seat, handIndex));
        }

        /// <summary>
        /// Is this hand drawn TUCKED? For the badge drivers, which place themselves against the hand and need their own
        /// tucked-hand placement. A single hand is never tucked, so the card count is part of the question.
        /// </summary>
        public bool IsHandTucked(int seat, int handIndex, int handCount) => handCount > 1 && IsTucked(seat, handIndex);

        // EDITOR ONLY (CardAnchorGizmo): -1 = no override, else a BITMASK of hand indices drawn as finished, so the
        // tuck can be tuned without dealing a real split. A mask, not a single index, because both hands of a played-out
        // split are tucked at once and that is the layout worth eyeballing.
        private int _previewTuckMask = -1;

        /// <summary>
        /// Editor preview hook: draw the hands in <paramref name="handMask"/> (bit 0 = hand 0, bit 1 = hand 1, …) as if
        /// they had finished playing. Pass -1 for no override. No-op in Play mode — the live table is board-driven and
        /// must never take its layout from a scene-authoring toggle.
        /// </summary>
        public void SetPreviewTuck(int handMask)
        {
            if (Application.isPlaying) return;
            _previewTuckMask = handMask;
        }

        /// <summary>
        /// Recompute which split hands are drawn tucked. Called from <see cref="Render"/> ONLY, and after its
        /// round-end-hold guard: the tuck changes where cards sit, and the value / result badges read the same geometry
        /// every LateUpdate, so changing it at a moment the cards are NOT re-laid would leave every badge floating away
        /// from its hand. Tying it to the render keeps the two in lockstep.
        /// </summary>
        private void RefreshTucked(BoardSnapshot board)
        {
            _tucked.Clear();
            BuildTuckSet(board, _tucked);
        }

        /// <summary>
        /// Which split hands have earned the tuck. <c>Done</c> alone is NOT enough: the server marks a hand finished
        /// the instant it resolves, which for a bust or a 21 is while the card that ended it is still parked at the
        /// shoe or mid-flight. Tucking then shrank the hand away before the player had seen the card or its badge. So a
        /// hand also has to have its LAST CARD LANDED, and then sit for <see cref="tuckDelaySeconds"/>.
        /// </summary>
        private void BuildTuckSet(BoardSnapshot board, HashSet<int> into)
        {
            if (!tuckPlayedHands || board?.Seats == null) return;

            foreach (var seat in board.Seats)
            {
                var hands = seat?.Player?.Hands;
                if (hands == null || hands.Count < 2) continue;   // only a SPLIT ever crowds a seat
                for (int h = 0; h < hands.Count; h++)
                {
                    var hand = hands[h];
                    if (hand == null || !hand.Done) continue;

                    int key0 = HandKey(seat.SeatNumber, h);
                    // Leaving the felt shortly — don't tuck it on the way out (see MarkHandClearing).
                    if (_clearingHands.Contains(key0) || _clearedHands.Contains(key0)) continue;

                    int n = hand.Cards != null ? hand.Cards.Count : 0;
                    if (n == 0 || !CardSettled(seat.SeatNumber, h, n - 1)) continue;   // final card still in the air

                    int key = key0;
                    if (!_tuckReadyAt.TryGetValue(key, out float readyAt))
                    {
                        readyAt = Time.unscaledTime + Mathf.Max(0f, tuckDelaySeconds);
                        _tuckReadyAt[key] = readyAt;
                    }
                    if (Time.unscaledTime >= readyAt) into.Add(key);
                }
            }
        }

        // When each finished hand's last card landed (+ the delay). Cleared at the start of every round.
        private readonly Dictionary<int, float> _tuckReadyAt = new Dictionary<int, float>();
        private readonly HashSet<int> _tuckScratch = new HashSet<int>();
        private BoardSnapshot _lastBoard;

        /// <summary>
        /// The tuck now depends on things that change BETWEEN board pushes — a card landing, and a timer — so it can no
        /// longer be computed only when the server speaks. Re-check it per frame and, when it changes, re-run the
        /// layout: that is what actually glides the cards into (or out of) the tucked pose, and it keeps the badges,
        /// which read the same geometry, moving with them rather than floating off.
        /// </summary>
        private void RetuckIfChanged()
        {
            if (_lastBoard == null || _roundEndHeld) return;
            _tuckScratch.Clear();
            BuildTuckSet(_lastBoard, _tuckScratch);
            if (_tuckScratch.SetEquals(_tucked)) return;
            Render(_lastBoard);   // idempotent — the whole view is built to absorb a repeated push
        }

        /// <summary>
        /// The card's LIVE anchor-local pose WHILE THE LAYOUT IS MOVING IT — where it is right now, mid-glide, rather
        /// than the pose it is heading for. Labels pinned to a card use this so they travel WITH it through a fan
        /// re-centre or a tuck instead of jumping to the destination and waiting for the card to catch up.
        ///
        /// Returns FALSE unless a <see cref="CardMover"/> is actually running, and that restriction is the point. The
        /// card transform is not owned by the layout alone — <c>CardFlip</c> writes to it too, for the dealer's peek
        /// tilt and the hole-card reveal (turn, lift, scale pop, overshoot). Reading it unconditionally made every
        /// value and result badge ride along with that juice, tilting and lifting with the card it labels. Those
        /// animations run with the mover idle, so gating on the mover cleanly separates "the layout moved the card"
        /// (badge should follow) from "the card is being animated in place" (badge must not).
        ///
        /// <paramref name="scale"/> is the uniform multiplier on the card prefab's own scale, so an offset authored
        /// against a full-size card can be scaled by it exactly as <see cref="CardLocalTRS"/> reports.
        /// </summary>
        public bool TryCardPose(int seat, int handIndex, int cardIndex, out Vector3 pos, out Quaternion rot, out float scale)
        {
            pos = Vector3.zero; rot = Quaternion.identity; scale = 1f;
            int key = SlotKey(seat, handIndex, cardIndex);
            if (_pendingDeal.Contains(key)) return false;                                      // parked at the shoe — not on the felt
            if (!_rendered.TryGetValue(key, out var slot) || slot.Card == null) return false;
            if (slot.Mover == null || !slot.Mover.Animating) return false;                      // at rest (or being juiced) — use the authored layout pose

            var tr = slot.Card.transform;
            pos = tr.localPosition;
            rot = tr.localRotation;
            scale = _cardBaseScale.x > 0.0001f ? tr.localScale.x / _cardBaseScale.x : 1f;
            return true;
        }

        /// <summary>
        /// Anchor-local CENTRE of hand <paramref name="handIndex"/> when a seat plays <paramref name="handCount"/>
        /// hands (a split). Hands are centred about the seat anchor and gapped by <c>splitHandStep</c>: 1 hand sits
        /// at the anchor, 2 split hands straddle it symmetrically (a clear gap between them). The card fan, the
        /// active-hand glow, and the per-hand bet stacks all build around this point.
        /// </summary>
        public Vector3 HandCenterLocal(int handIndex, int handCount)
            => splitHandStep * (handIndex - (handCount - 1) * 0.5f);

        /// <summary>
        /// The hand's centre AS DRAWN — <see cref="HandCenterLocal"/> plus the tuck, if this hand is tucked.
        ///
        /// Split Hand Step has to hold two FULL-SIZE hands apart, so the moment one shrinks that spacing reads as a
        /// hole in the middle of the seat; the tucked hand slides back toward the anchor by <c>tuckPullIn</c> (scaling
        /// its own centred offset, so it closes symmetrically for any number of hands) and then by <c>tuckNudge</c>.
        ///
        /// This is what a per-HAND label anchors to — stable however many cards the hand holds, unlike the last card,
        /// which walks sideways as the fan grows. The BET STACKS deliberately read <see cref="HandCenterLocal"/>
        /// instead: the wager stays where it was placed and where the round-end director collects and pays it.
        /// </summary>
        public Vector3 HandCenterDrawn(int seat, int handIndex, int handCount)
        {
            Vector3 centre = HandCenterLocal(handIndex, handCount);
            if (handCount > 1 && IsTucked(seat, handIndex))
                centre = centre * (1f - Mathf.Clamp01(tuckPullIn)) + tuckNudge;
            return centre;
        }

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
            // Once a card has touched down it STAYS landed. It moves again plenty of times after the deal — the fan
            // re-centres on every hit, and a finished split hand tucks away — and treating those as "not dealt yet"
            // made every badge on the hand blink out for the duration of the glide and pop back in.
            if (_landed.Contains(key)) return true;
            return slot.Mover == null || !slot.Mover.Animating;                               // landed (not gliding in)
        }

        /// <summary>
        /// A card has just TOUCHED DOWN on the felt after flying in — seat (0 = dealer) and the world point it landed
        /// at. The moment for a card sound, and the only one the view didn't already expose: the dealer's throw has a
        /// clip event, but the landing happens on the mover's own schedule with nothing to hang off.
        ///
        /// Fires ONCE per card per round, and only for a card that actually FLEW. It stays silent for a re-layout (the
        /// fan re-centring on a hit, a hand tucking) and for a SNAP — a mid-round join reveals every already-dealt card
        /// in one frame, which would otherwise fire eight sounds at once for cards dealt before you sat down.
        /// </summary>
        public event System.Action<int, Vector3> CardLanded;

        // A card is LANDED the first time it is on the felt and not moving. Swept per frame (a handful of cards) rather
        // than hooked off the mover, so it is true regardless of which path put the card there — thrown by the dealer,
        // self-dealt, snapped on a mid-round join, or laid out by the round-end director.
        private void MarkLanded()
        {
            foreach (var kv in _rendered)
            {
                int key = kv.Key;
                if (_pendingDeal.Contains(key)) continue;

                var mover = kv.Value.Mover;
                if (mover != null && mover.Animating) { _flying.Add(key); continue; }   // in transit — remember that it flew

                bool flew = _flying.Remove(key);
                if (!_landed.Add(key)) continue;   // already down; this was a re-layout settling, not a landing

                if (flew && CardLanded != null && kv.Value.Card != null)
                    CardLanded.Invoke(key / 10000, kv.Value.Card.transform.position);   // SlotKey packs seat * 10000
            }
        }

        private readonly HashSet<int> _flying = new HashSet<int>();   // keys seen mid-move, so a snap can be told from a real deal-in

        /// <summary>
        /// True while ANY card is still coming in — parked at the shoe/hand awaiting its throw, or gliding to its spot.
        /// Used for the WHOLE-TABLE gate: the round-end sweep waits on this so the felt clears only after the last card
        /// lands. (Per-player UI gates on <see cref="SeatSettled"/> instead, so it isn't held by a remote seat.)
        /// </summary>
        public bool AnyCardAnimating()
        {
            if (_pendingDeal.Count > 0) return true;                        // parked, awaiting a throw/stagger
            foreach (var kv in _rendered)
            {
                var mover = kv.Value.Mover;
                if (mover != null && mover.Animating) return true;         // gliding in
            }
            return false;
        }

        /// <summary>
        /// True when SEAT's cards are all on the felt — none parked, none gliding. The LOCAL player's turn-driven UI
        /// (actions, decision camera, insurance) gates on its OWN seat (+ seat 0 = dealer) so it isn't held hostage by a
        /// REMOTE seat's animation, and its own decision window (e.g. insurance) isn't eaten by the rest of the table's
        /// deal. SlotKey packs seat as key/10000.
        /// </summary>
        public bool SeatSettled(int seat)
        {
            foreach (var key in _pendingDeal) if (key / 10000 == seat) return false;   // a card for this seat still parked
            foreach (var kv in _rendered)
            {
                if (kv.Key / 10000 != seat) continue;
                if (kv.Value.Mover != null && kv.Value.Mover.Animating) return false;   // gliding in
            }
            return true;
        }

        /// <summary>
        /// True when the LOCAL decision inputs are on the felt: <paramref name="seat"/>'s own cards AND the dealer's
        /// (seat 0). The dealer is dealt LAST (see <see cref="DealOrder"/>), so the dealer being settled means the whole
        /// opening deal has finished — while still NOT waiting on OTHER players' seats, so a remote player's hit can
        /// never eat your turn timer. ALSO false while the dealer is PEEKING at her hole card, so you can't act (and the
        /// turn clock can't start) until she's finished checking. Decision UI (action buttons, turn prompt, decision
        /// camera, insurance) and the /presented turn-clock handshake all gate on this.
        /// </summary>
        /// ALSO false while a hand at this seat is being taken off the felt after a bust. Busting one half of a split
        /// hands the turn straight to the other half — correctly, server-side — but the dealer is still collecting the
        /// busted hand's chips and sweeping its cards, and prompting for the next decision over the top of that asks
        /// the player to look away from the thing being shown to them. Gating HERE rather than in
        /// <see cref="ActionReady"/> is deliberate: this is the gate the /presented handshake uses, so the clock stays
        /// on the server's generous ceiling for the length of the sweep instead of the player paying for it.
        public bool DecisionReady(int seat)
            => !_peekHeld && !HandClearingAtSeat(seat) && SeatSettled(seat) && SeatSettled(0);

        /// <summary>
        /// The gate for the DECISION PRESENTATION — the close camera framing and the action buttons. It is
        /// <see cref="DecisionReady"/> plus "no card anywhere on the felt is still moving".
        ///
        /// <see cref="DecisionReady"/> deliberately ignores other seats so a remote player's think time can never eat
        /// your turn clock. That is right for the CLOCK, but wrong for the PICTURE: your turn can begin while the
        /// dealer is still throwing the previous player's hit, and the camera would rush in over a card in flight.
        ///
        /// Everything that presents the decision must use THIS, and nothing may use one of the two gates while its
        /// neighbour uses the other — that is what made the camera arrive before the buttons lit. Bounded, not open
        /// ended: parked cards are released by the deal pump's own timeout and movers are finite, so this always
        /// clears on its own.
        /// </summary>
        public bool ActionReady(int seat) => DecisionReady(seat) && !AnyCardAnimating();

        /// <summary>True while the dealer's PEEK is playing — see <see cref="DecisionReady"/>.</summary>
        public bool PeekHeld => _peekHeld;

        /// <summary>The dealer peek takes the decision gate for its duration, so the player can't act mid-peek.
        /// ALWAYS pair with <see cref="EndPeek"/> (the peek driver does so, including on teardown).</summary>
        public void BeginPeek() => _peekHeld = true;

        /// <summary>Release the peek hold — decisions (and the turn clock) resume.</summary>
        public void EndPeek() => _peekHeld = false;

        /// <summary>
        /// True while the round-END presentation is still playing out — the final/bust card is still landing, the
        /// deferred sweep is armed (roundEndHold), or cards are gliding to the discard. Phase UI (the bet-pose camera)
        /// holds off returning to the betting framing until this clears, so it doesn't pull back mid-bust.
        /// </summary>
        public bool RoundEndSettling => _roundEndHeld || _collectPending || AnyCardAnimating();

        /// <summary>True while a <see cref="RoundEndDirector"/> owns the felt (Render frozen, deferred sweep cancelled).
        /// The deal conductor skips board pushes while this is set, so it can't advance its per-seat baseline off pushes
        /// the frozen Render dropped; the director re-baselines it on release.</summary>
        public bool RoundEndHeld => _roundEndHeld;

        /// <summary>True once the director has actually FLIPPED the dealer's hole card this round-end (see
        /// <see cref="RevealDealerHole"/>); cleared when it takes over (<see cref="BeginRoundEnd"/>). The settle board
        /// carries the dealer's FULL total the instant the round resolves, so score / blackjack badges gate on this —
        /// otherwise they'd print the hidden card's value before it is visually turned.</summary>
        public bool DealerHoleRevealed { get; private set; }

        /// <summary>The shoe / dealer-hand point cards deal from — the round-end director uses it as the fallback hub for
        /// collect (chips fly here) and pay (chips fly from here) when no dedicated dealer-hand anchor is assigned.</summary>
        public Transform DealSource => dealSource;

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
            if (_roundEndHeld) return;   // the round-end director owns the felt — ignore board pushes until it releases the hold

            // A NEW round wipes the per-hand tuck timers, so last round's hands can't tuck instantly this round — and
            // the mid-round clears, or a seat that busted last round would never show cards again.
            if (board.RoundInProgress && !_prevRenderInRound)
            {
                _tuckReadyAt.Clear();
                _clearedHands.Clear();
                _clearingHands.Clear();
            }
            _prevRenderInRound = board.RoundInProgress;
            _lastBoard = board;          // so the per-frame tuck re-check has a board to lay out from

            ReclaimMovedCards(board);    // SPLIT: migrate the moved card to its new slot BEFORE positional layout
            RefreshTucked(board);        // finished split hands compact — must change only where the felt is re-laid

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
                        // Highest hand index FIRST. On a split that hand sits on the player's right, and the server
                        // both deals and plays it first (BlackjackTableManager.GetOrderedHands) — new cards enter the
                        // seat's deal queue in this order, so walking up from 0 would fly the left hand's card first
                        // and read as the dealer serving against the table's right→left direction. Where each card
                        // LANDS comes from its hand index, not from this order, so only the sequence changes.
                        for (int h = hands.Count - 1; h >= 0; h--)
                        {
                            // A hand CLEARED mid-round (a bust the dealer has already taken) is deliberately not
                            // desired any more, so the stale-sweep below flies its cards to the discard. The SERVER
                            // still lists them — it keeps every hand on the board until the round settles — so without
                            // this the very next push would deal them straight back onto the felt.
                            if (_clearedHands.Contains(HandKey(seat.SeatNumber, h))) continue;
                            LayOutHand(hands[h].Cards, anchor, seat.SeatNumber, h, hands.Count);
                        }
                    }
                }
            }

            // Deal this pass's new cards in ONE BY ONE, in real dealing order. With a conductor present each card waits
            // in its seat's release queue for the dealer's throw; otherwise the view self-deals on its own stagger.
            ScheduleNewCards();

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

        private bool _prevRenderInRound;

        private void Update()
        {
            MarkLanded();
            RetuckIfChanged();

            // Complete a deferred round-end sweep once the last card has LANDED, then hold roundEndHold so the final
            // hand (e.g. a bust) is readable. Render is board-driven, but a bust/final card lands BETWEEN pushes —
            // without this the felt would clear the instant the server resolves.
            if (!_collectPending) { _collectReadyAt = -1f; return; }
            if (AnyCardAnimating()) { _collectReadyAt = -1f; return; }
            // roundEndHold exists so a bust/final card is readable before the felt clears at the END of a round. A
            // MID-round clear (a busted hand the dealer has already taken) has had its own read beat already, and
            // holding here would keep RoundEndSettling true — freezing the NEXT player's buttons and camera for no
            // reason while they are trying to act.
            bool midRound = _lastBoard != null && _lastBoard.RoundInProgress;
            if (_collectReadyAt < 0f)
            {
                _collectReadyAt = Time.unscaledTime + (midRound ? 0f : Mathf.Max(0f, roundEndHold));
                return;
            }
            if (Time.unscaledTime < _collectReadyAt) return;

            _collectPending = false; _collectReadyAt = -1f;
            _stale.Clear();
            foreach (var kv in _rendered)
                if (!_desired.Contains(kv.Key)) _stale.Add(kv.Key);
            if (_stale.Count > 0) CollectStale();
        }

        // SPLIT reconciliation. The view keys cards positionally — (seat, hand, cardIndex). A split re-shapes the hands:
        // hand H's 2nd card becomes hand H+1's 1st card (same PHYSICAL card). Positional layout alone would MORPH the old
        // slot's face into the freshly-dealt card AND deal the moved card again at its new slot. So BEFORE layout, find any
        // already-rendered card whose identity now belongs at a DIFFERENT slot (same seat) with no card there yet, and
        // RE-KEY it — then LayOutHand just glides it across, and the genuinely new card deals into the freed slot.
        private void ReclaimMovedCards(BoardSnapshot board)
        {
            if (board == null || !board.RoundInProgress || board.Seats == null) return;   // splits only happen mid-round

            // desired key -> data for the whole live board
            _desiredData.Clear();
            if (board.Dealer?.Cards != null)
                for (int i = 0; i < board.Dealer.Cards.Count; i++)
                    _desiredData[SlotKey(0, 0, i)] = board.Dealer.Cards[i];
            foreach (var seat in board.Seats)
            {
                if (seat?.Player?.Hands == null || AnchorForSeat(seat.SeatNumber) == null) continue;
                var hands = seat.Player.Hands;
                for (int h = 0; h < hands.Count; h++)
                {
                    var cards = hands[h].Cards;
                    if (cards == null) continue;
                    for (int i = 0; i < cards.Count; i++)
                        _desiredData[SlotKey(seat.SeatNumber, h, i)] = cards[i];
                }
            }

            // movable = a rendered, NON-parked card that is NOT already correctly placed (its slot's desired card differs
            // or its slot is gone). These are the candidates a split may have relocated.
            _movableKeys.Clear();
            foreach (var kv in _rendered)
            {
                if (_pendingDeal.Contains(kv.Key)) continue;
                if (_desiredData.TryGetValue(kv.Key, out var want) && SameCard(kv.Value.Data, want)) continue;
                _movableKeys.Add(kv.Key);
            }
            // For each desired slot that is EMPTY (no rendered card), claim a movable card of matching identity from the
            // SAME seat and re-key it there. Only fill empty slots — occupied ones are left to LayOutHand.
            foreach (var d in _desiredData)
            {
                if (_movableKeys.Count == 0) break;            // nothing left to migrate — the common (no-split) path
                int destKey = d.Key;
                if (_rendered.ContainsKey(destKey)) continue;   // occupied → LayOutHand reconciles it
                int destSeat = destKey / 10000;

                for (int m = 0; m < _movableKeys.Count; m++)
                {
                    int srcKey = _movableKeys[m];
                    if (srcKey / 10000 != destSeat) continue;
                    if (!_rendered.TryGetValue(srcKey, out var slot) || !SameCard(slot.Data, d.Value)) continue;
                    _rendered.Remove(srcKey);
                    _rendered[destKey] = slot;    // move the REAL card; LayOutHand will glide it to the new fan spot
                    // The card itself is already on the felt, so carry its landed flag across with it — otherwise the
                    // split's moved card counts as undealt and the new hand's badges wait on a deal that never happens.
                    if (_landed.Remove(srcKey)) _landed.Add(destKey);
                    _movableKeys.RemoveAt(m);
                    break;
                }
            }

            // POSITIONAL pass — for the migration identity is BLIND to.
            //
            // The movable test above asks "does this card's own slot still want this face?". In a 6-deck shoe the card
            // freshly dealt into the vacated slot can carry the SAME face as the card that just moved out of it: split
            // 10♥/Q♠ and draw another Q♠ to the first hand, and slot (hand 0, card 1) still wants a Q♠ — so the ORIGINAL
            // Q♠ sitting there looks correctly placed, is never offered as movable, and the loop above finds nothing to
            // do. The moved card is then treated as brand new: it is re-dealt from the shoe, and the second hand sits
            // EMPTY until the dealer throws it a card it was already holding.
            //
            // SameCard cannot break that tie — the two cards are indistinguishable, which is exactly the point. Position
            // can, because the split rule is fixed and stated at the top of this method: hand H's 2nd card IS hand H+1's
            // 1st card. Claim it by that rule instead of by face.
            //
            // Moving a card out of an already-satisfied slot is safe: it empties one slot and fills another, so the
            // number of cards still to be dealt does not change — the vacated slot simply receives the genuinely new
            // card, which is the one that should have been dealt in the first place.
            foreach (var d in _desiredData)
            {
                int destKey = d.Key;
                if (_rendered.ContainsKey(destKey)) continue;      // already placed (usually by the pass above)

                int hand = (destKey / 100) % 100;
                if (hand <= 0 || destKey % 100 != 0) continue;     // only a split-created hand's FIRST card migrates

                int srcKey = SlotKey(destKey / 10000, hand - 1, 1);   // the previous hand's second card
                if (_pendingDeal.Contains(srcKey)) continue;          // still parked at the shoe — not on the felt yet
                if (!_rendered.TryGetValue(srcKey, out var slot) || !SameCard(slot.Data, d.Value)) continue;

                _rendered.Remove(srcKey);
                _rendered[destKey] = slot;
                if (_landed.Remove(srcKey)) _landed.Add(destKey);
            }
        }

        // Drain this pass's freshly-parked cards (sorted into real deal order) into either the conductor's per-seat
        // release FIFO (dealer throws them one per throw) or the view's own staggered self-deal. Extracted from Render
        // so the round-end director can park the dealer's final draws (LayOutDealerFinal) and route them the same way.
        private void ScheduleNewCards()
        {
            if (_newThisPass.Count == 0) return;
            _newThisPass.Sort((a, b) => a.Order.CompareTo(b.Order));
            if (ConductorActive)
                for (int i = 0; i < _newThisPass.Count; i++) EnqueueForRelease(_newThisPass[i]);
            else
                for (int i = 0; i < _newThisPass.Count; i++)
                    StartCoroutine(DealRoutine(_newThisPass[i], i * Mathf.Max(0f, dealStagger)));
            _newThisPass.Clear();
        }

        private static CardMover EnsureMover(CardVisual card)
        {
            var m = card.GetComponent<CardMover>();
            return m != null ? m : card.gameObject.AddComponent<CardMover>();
        }

        // Real dealing order for a card: round by round (cardIndex), players (seat asc) then the dealer (seat 0) LAST.
        // One card per seat per round (cardIndex is the primary key), dealt RIGHT-to-left within each round: blackjack
        // first base = the dealer's left = the player's RIGHTMOST seat = the HIGHEST seat number, dealt first; dealer
        // (seat 0) last. Higher seat number => smaller order => earlier. Must match the server's GetOrderedHands + DealerAnimator.
        private static int DealOrder(int seat, int cardIndex) => cardIndex * 1000 + (seat == 0 ? 999 : 100 - seat);

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

        /// <summary>
        /// The felt is being SWEPT — every remaining card is leaving for the discard, as one gesture. Fires once per
        /// sweep (not per card) with the discard tray's world position. Both sweep paths funnel through
        /// <see cref="CollectStale"/>, so this covers the director's <see cref="SweepNow"/> and the no-director
        /// deferred sweep alike.
        /// </summary>
        public event System.Action<Vector3> CardsSwept;

        // Armed when a sweep begins, consumed by the first card that starts travelling — so one sweep still makes
        // exactly one sound, but at the moment the cards actually move rather than when the gesture was decided.
        private bool _sweepAnnouncePending;
        private Vector3 _sweepAnnouncePoint;

        // At least one card in the sweep was actually on screen. A parked, never-revealed card is reclaimed silently.
        private static bool AnyVisible(Dictionary<int, List<CardVisual>> groups)
        {
            foreach (var g in groups.Values)
                for (int i = 0; i < g.Count; i++)
                    if (g[i] != null && g[i].gameObject.activeSelf) return true;
            return false;
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
                _landed.Remove(key);   // the slot is free again — a later round reusing this SlotKey must re-earn "landed"
                _flying.Remove(key);
                int seat = key / 10000;   // SlotKey packs seat*10000 + hand*100 + cardIndex
                if (!groups.TryGetValue(seat, out var list)) { list = new List<CardVisual>(); groups[seat] = list; }
                list.Add(card);
            }

            PurgeReleaseQueues();   // drop collected cards from the release FIFOs so a reused SlotKey can't fly a pooled card

            // ONE announcement for the whole sweep, not one per card. Every card leaving is a single gesture — the
            // seats go one after another on collectStagger and the sound covers the lot. Only fires if a card was
            // actually VISIBLE: entering a table whose last round is still lingering releases hidden, never-dealt
            // cards, and a sweep sound for a felt the player never saw is a sound from nowhere.
            //
            // ARMED here, FIRED by the first card that actually starts travelling (see CollectRoutine). It used to
            // fire right here, which was the same instant back when a card's whole sweep was the flight. Now the
            // flourish puts a lift and a turn in front of that, so firing here plays a sweep over a card that has not
            // begun to move — the sound describes the wrong gesture. The sound belongs to the travel, so the travel
            // starts it.
            _sweepAnnouncePending = CardsSwept != null && AnyVisible(groups);
            _sweepAnnouncePoint = discardTarget != null ? discardTarget.position : transform.position;

            if (discardTarget == null || collectSeconds <= 0f)
            {
                // No flight at all — the cards just vanish, so there is no later moment to wait for.
                if (_sweepAnnouncePending) { _sweepAnnouncePending = false; CardsSwept.Invoke(_sweepAnnouncePoint); }
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

            // LIFT, then TURN FACE-DOWN, then leave. A card sliding off the felt still showing its face reads as the
            // table deleting it; a dealer picks it up, turns it over, and only then sweeps it — the turn is what says
            // the hand is finished. Skipped for a card that is already face-down (the dealer's unrevealed hole on a
            // round that ended early): there is nothing to turn, and turning it would flash the face.
            if (sweepTurnsFaceDown && card.Current.FaceUp)
            {
                if (sweepLift > 0f && sweepLiftSeconds > 0f)
                {
                    // Overshoot, then settle. CardMover eases OUT of every move, so a single Target glides to the
                    // height and stops dead — precise, and completely inert. Throwing it past and letting it drop
                    // back is what makes the card feel picked up rather than repositioned, and it costs one extra
                    // short move rather than a new easing system.
                    Vector3 rest = tr.localPosition;
                    Vector3 top = rest + Vector3.up * (sweepLift * (1f + sweepLiftOvershoot));

                    EnsureMover(card).Target(top, tr.localRotation, tr.localScale, sweepLiftSeconds);
                    yield return new WaitForSecondsRealtime(sweepLiftSeconds);
                    if (card == null) yield break;

                    if (sweepLiftOvershoot > 0f && sweepSettleSeconds > 0f)
                    {
                        EnsureMover(card).Target(rest + Vector3.up * sweepLift, tr.localRotation, tr.localScale,
                                                 sweepSettleSeconds);
                        yield return new WaitForSecondsRealtime(sweepSettleSeconds);
                        if (card == null) yield break;
                    }
                }

                // The SAME flip the hole-card reveal uses, run the other way: the art swaps at the apex while the card
                // is edge-on, so the face is never seen mid-turn. Its own juice, not the dealer's — the sweep wants a
                // springier turn than a reveal, and a reveal must stay exactly as authored.
                if (sweepFlipSeconds > 0f)
                {
                    yield return CardFlip.On(card.gameObject)
                                         .Reveal(() => { if (card != null) card.SetFaceUp(false); },
                                                 sweepFlipSeconds, sweepFlipEdgeEuler, sweepFlipJuice);
                    if (card == null) yield break;
                }
            }

            // THE TRAVEL STARTS HERE — so the sweep sound starts here, on whichever card gets moving first. Fired from
            // the card rather than on a computed delay because a face-down card skips the lift and turn entirely: it
            // leaves immediately, and a timer sized for the flourish would play its sound long after it had gone.
            if (_sweepAnnouncePending)
            {
                _sweepAnnouncePending = false;
                CardsSwept?.Invoke(_sweepAnnouncePoint);
            }

            var parent = tr.parent;   // still its anchor — convert the world discard point into that local frame
            Vector3 local = parent != null ? parent.InverseTransformPoint(discardTarget.position) : tr.localPosition;
            EnsureMover(card).Target(local, tr.localRotation, tr.localScale * (1f - collectShrink), collectSeconds);
            yield return new WaitForSecondsRealtime(collectSeconds);
            _pool.Release(card);
        }

        private void LayOutHand(List<CardView> cards, Transform anchor, int seat, int handIndex, int handCount, bool snapNew = false)
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
                        // identical push doesn't restart it; an in-flight deal-in keeps its own target). A hand being
                        // TUCKED travels much further than a re-centre — shrinking and sliding in toward the seat — so
                        // it gets its own, longer duration.
                        float glide = (handCount > 1 && IsTucked(seat, handIndex)) ? tuckSeconds : recenterSeconds;
                        EnsureMover(slot.Card).Target(pos, rot, targetScale, glide);
                        if (!SameCard(slot.Data, data)) { slot.Card.SetCard(data); slot.Data = data; _rendered[key] = slot; }
                    }
                }
                else
                {
                    var card = _pool.Rent(anchor);
                    if (skin != null) card.Skin = skin;
                    card.SetCard(data);
                    var mover = EnsureMover(card);
                    _rendered[key] = new Slot { Card = card, Data = data, Mover = mover };   // cache the mover (no per-frame GetComponent)
                    _landed.Remove(key);   // a genuinely NEW card at this slot has not landed, whatever used to live here

                    if (snapNew)
                    {
                        // Round-end: a FINAL card that arrived on the settle push (e.g. a bust that ends the round)
                        // appears in place — no deal-in and no parking, since the director isn't throwing player cards.
                        mover.Snap(pos, rot, targetScale);
                    }
                    else if (dealSource != null && dealSeconds > 0f && (ConductorActive || dealStagger > 0f))
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

        // ---- Round-end director commands (ONLY a RoundEndDirector calls these; see that class) ----
        // While the director HOLDS, it owns the felt: Render is frozen (above) so the view never independently reacts
        // to the settle push — it just exposes these small commands the director drives in sequence.

        /// <summary>
        /// The director takes over for the settle sequence: FREEZE board-driven render and CANCEL the deferred sweep the
        /// just-processed settle push armed, so the final hands stay on the felt until the director plays it out.
        /// <see cref="RoundEndSettling"/> stays latched (the camera keeps holding the bet framing) for the whole hold.
        /// </summary>
        public void BeginRoundEnd()
        {
            _roundEndHeld = true;
            DealerHoleRevealed = false;   // hole is down again until the reveal beat turns it — badges hold their total
            _collectPending = false;   // cancel the settle-push sweep synchronously, before Update() can run it
            _collectReadyAt = -1f;
        }

        /// <summary>Release the director's hold — board pushes render again. Cards are already gone via <see cref="SweepNow"/>.</summary>
        public void EndRoundEnd() => _roundEndHeld = false;

        /// <summary>
        /// Snap every seated player's FINAL hand into place (no deal-in, no parking) — covers a round-ender card that
        /// arrived on the SAME settle push (e.g. a bust that ends the round), which the gated <see cref="Render"/>
        /// (cards only while RoundInProgress) skipped. Existing cards are left as-is; only genuinely new cards snap in.
        /// The dealer is handled separately by <see cref="RevealDealerHole"/> + <see cref="LayOutDealerFinal"/>.
        /// </summary>
        public void ShowFinalPlayerHands(BoardSnapshot board)
        {
            if (board?.Seats == null) return;
            foreach (var seat in board.Seats)
            {
                if (seat?.Player == null) continue;
                var anchor = AnchorForSeat(seat.SeatNumber);
                if (anchor == null) continue;
                var hands = seat.Player.Hands;
                if (hands == null) continue;
                for (int h = 0; h < hands.Count; h++)
                {
                    // A hand already taken off the felt mid-round STAYS off. The server still reports a busted hand in
                    // the settle snapshot — that is correct, it is part of the result — but BustHandCleaner has already
                    // collected its chips and swept its cards, and its card objects are out of _rendered. Laying it out
                    // again here counts every one of them as brand new, so the whole hand is re-instantiated: a hitch
                    // while the pool refills, a landing sound, and a hand the player watched leave reappearing on the
                    // felt just as the dealer goes to turn her hole card.
                    //
                    // Render's desired-build applies the same guard; this path bypasses Render entirely (the director
                    // holds it), which is exactly why it needs its own.
                    if (_clearedHands.Contains(HandKey(seat.SeatNumber, h))) continue;
                    LayOutHand(hands[h].Cards, anchor, seat.SeatNumber, h, hands.Count, snapNew: true);
                }
            }
        }

        /// <summary>
        /// The dealer's currently-rendered HOLE card (seat 0, card index 1) — the face-down card the reveal beat flips.
        /// Null if the dealer has fewer than 2 cards on the felt.
        /// </summary>
        public CardVisual DealerHoleCard()
            => _rendered.TryGetValue(SlotKey(0, 0, 1), out var slot) ? slot.Card : null;

        /// <summary>
        /// Swap the dealer hole card (seat 0, index 1) to its revealed face. The director calls this at the APEX of the
        /// reveal flip (so the art changes while the card is edge-on), or directly as a fallback when the card isn't
        /// visible. No-op if the hole card isn't rendered.
        /// </summary>
        public void RevealDealerHole(CardView revealed)
        {
            if (revealed == null) return;
            DealerHoleRevealed = true;   // badges may now show the dealer's real total
            int key = SlotKey(0, 0, 1);
            if (!_rendered.TryGetValue(key, out var slot) || slot.Card == null) return;
            slot.Card.SetCard(revealed);
            slot.Data = revealed;
            _rendered[key] = slot;
        }

        /// <summary>
        /// Lay out the dealer's FINAL hand: her opening two cards glide to re-centre and every card BEYOND them (her
        /// draws to 17) PARKS at the shoe like a normal deal, queued under seat 0. Returns how many draws were parked so
        /// the director throws exactly that many through the deal conductor. The hole card is NOT revealed here —
        /// <see cref="RevealDealerHole"/> owns that. If she stands (no draws) this parks nothing and returns 0. If no
        /// deal source is configured the draws simply snap in place (and 0 is returned — nothing to throw).
        /// </summary>
        public int LayOutDealerFinal(DealerView dealer)
        {
            if (dealer?.Cards == null || dealerAnchor == null) return 0;
            int before = CountPendingForSeat(0);
            LayOutHand(dealer.Cards, dealerAnchor, 0, 0, 1);   // new cards (index >= 2) park at the shoe
            ScheduleNewCards();                                 // hand the parked draws to seat 0's release FIFO
            return CountPendingForSeat(0) - before;
        }

        /// <summary>
        /// Sweep EVERY card on the felt to the discard immediately (the director's final beat). Bypasses the board diff —
        /// at round end all hands leave regardless of what the last snapshot still carries.
        /// </summary>
        public void SweepNow()
        {
            _collectPending = false;
            _collectReadyAt = -1f;
            _stale.Clear();
            foreach (var kv in _rendered) _stale.Add(kv.Key);
            if (_stale.Count > 0) CollectStale();
        }

        /// <summary>Rough seconds for <see cref="SweepNow"/> to play out — the director waits this before dropping the hold.</summary>
        public float SweepDuration => Mathf.Max(0f, collectSeconds) + Mathf.Max(0f, collectStagger);

        // Cards still PARKED (awaiting a throw) for a seat — the delta across a LayOutDealerFinal call is the draw count.
        private int CountPendingForSeat(int seat)
        {
            int n = 0;
            foreach (var key in _pendingDeal) if (key / 10000 == seat) n++;
            return n;
        }

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
