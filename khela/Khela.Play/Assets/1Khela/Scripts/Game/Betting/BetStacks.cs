using System.Collections;
using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Renders each seat's COMMITTED bet as chip stacks on the felt, driven by the live board — so every player sees
    /// every seat's wager (the board is server-authoritative and pushed to all clients). Split-aware: a seat with two
    /// hands shows TWO stacks, one under each hand, positioned with the SAME centred-split offset the cards use
    /// (<see cref="BlackjackTableView.HandCenterLocal"/>) so each stack sits beneath its hand. A stack appears at the
    /// deal, sits through the round, and clears at settle.
    ///
    /// One WORLD anchor per seat (place on the betting box, flat on the felt — chips stack along its local +Y; orient
    /// it like that seat's CARD anchor so the split offset lines up). The per-hand split is DERIVED, so you don't
    /// author one anchor per hand. Amounts are decomposed into the table's chip denominations and stacked. Put this on
    /// an always-active object. Stacks are visual-only (colliders disabled) so they never block chip drag. The TOP
    /// chip of each stack shows the stack's TOTAL value (the chips below keep their denomination, but they're hidden).
    /// </summary>
    public sealed class BetStacks : MonoBehaviour
    {
        // Per-seat hands we draw stacks for (decision: 2). A rare re-split to 3/4 still lays out cards; the extra
        // hands' bets just don't get a dedicated stack.
        private const int MaxHands = 2;

        [SerializeField] private TableController table;
        [SerializeField] private ChipSet chipSet;
        [Tooltip("The table view — provides the centred split offset so a 2nd hand's stack sits under the 2nd hand.")]
        [SerializeField] private BlackjackTableView tableView;
        [Tooltip("One world anchor per seat — element 0 = seat 1, … Place each on that seat's betting box. Chips " +
                 "stack along the anchor's local +Y, so orient it flat on the felt (matching that seat's CARD anchor facing).")]
        [SerializeField] private Transform[] anchorsBySeat;
        [Tooltip("Optional per-seat amount BADGE showing that seat's total on the felt. Element i = seat i+1. Assign " +
                 "the object carrying a BetAmountLabel (not the raw text) — it owns the pill's background too, and " +
                 "rolls itself open/closed, so an empty spot leaves no stray shape behind.")]
        [SerializeField] private BetAmountLabel[] labelsBySeat;
        [Tooltip("Vertical gap between stacked chips (local units).")]
        [SerializeField] private float stackStep = 0.02f;
        [Tooltip("Max chips drawn per stack — the amount is decomposed largest-denomination-first, so big bets stay short.")]
        [SerializeField] private int maxChips = 10;

        [Header("Gather-on-deal (pull the dropped pile into the committed stack)")]
        [Tooltip("Where the dropped chips are pulled from on DEAL. Auto-found by seat if left empty; element i = seat i+1.")]
        [SerializeField] private BetSpot[] betSpotsBySeat;
        [Tooltip("The bet builder — its DEAL fires the gather. Auto-found if unassigned.")]
        [SerializeField] private BetBuilder builder;
        [Tooltip("Stagger between each chip starting its pull into the stack (seconds).")]
        [SerializeField] private float gatherInterval = 0.06f;
        [Tooltip("How long one chip takes to fly into the stack (seconds).")]
        [SerializeField] private float gatherDuration = 0.28f;
        [Tooltip("Little mid-flight lift so a chip arcs into the stack instead of sliding flat (world units).")]
        [SerializeField] private float gatherArc = 0.05f;

        [Header("Tuck — a finished split hand's chips follow its cards")]
        [Tooltip("When the view tucks a played-out split hand (shrinks it and slides it in toward the seat), move and " +
                 "shrink that hand's chip stack with it. Off = the stack stays where the bet was placed while the " +
                 "cards move away from it.")]
        [SerializeField] private bool followTuck = true;
        [Tooltip("How much smaller a tucked hand's chips are drawn — 0.2 = 20% smaller. Also tightens the stack's " +
                 "vertical step by the same fraction, so a tall stack shrinks as a whole rather than stretching.")]
        [Range(0f, 0.6f)] [SerializeField] private float tuckChipShrink = 0.20f;
        [Tooltip("Extra shift for a TUCKED hand's chip stack, in the BET anchor's own frame (Z = toward the dealer, " +
                 "so a negative Z pulls the stack back toward the player and a positive Z closes the gap up to the " +
                 "cards). The hand-to-chips distance is authored as the gap between the card and bet anchors, and that " +
                 "was set for a FULL-SIZE hand — once the hand shrinks the same gap reads as too much empty felt, and " +
                 "the pull-in cannot close it because the cards and the chips move together. Nudge it here.")]
        [SerializeField] private Vector3 tuckChipNudge = new Vector3(0f, 0f, 0.2f);
        [Tooltip("Roughly how long the chips take to settle into the tucked pose. Keep it close to the view's Tuck " +
                 "Seconds so the chips and the cards travel together.")]
        [SerializeField] private float tuckFollowSeconds = 0.35f;

        [Header("Float-away — the stack shrinks out just before the win burst")]
        [Tooltip("How long the stack takes to shrink to nothing (seconds). ALL chips shrink together, so this is the " +
                 "whole effect length regardless of how many chips are on the spot. Keep it short (~0.2).")]
        [SerializeField] private float floatAwayShrinkSeconds = 0.2f;

        private List<GameObject>[] _stacks;   // flat: seatIdx * MaxHands + handIdx
        private long[] _lastAmount;            // flat, same indexing
        private int[] _lastHandCount;          // per seat — a change shifts the centred offsets, so force a rebuild

        // Round-end HOLD: when a RoundEndDirector is registered we do NOT clear the committed stacks at settle — the
        // director flies losers to the dealer, pays winners, then calls ReleaseHold. Held as a MonoBehaviour so there's
        // no hard type dependency on the director (mirrors the view's conductor). With NO director this stays inert and
        // the stacks clear at settle exactly as before.
        private MonoBehaviour _settleDirector;
        private bool _held;                       // the director owns our stacks (settle → done); OnBoard ignores pushes while set
        private bool _prevInRound;                // last board's RoundInProgress — to catch the in-round → settle transition
        private decimal _lastMinBet, _lastMaxBet; // stakes cached while live so BuildLooseStack decomposes winnings with the right denominations
        private int _gatheringSeat;               // 1-based seat whose dropped chips are mid-gather; OnBoard skips it until adopted
        private Coroutine _gatherRoutine;         // the running gather, so a rejected bet can cancel exactly that one
        private List<GameObject> _gatherChips;    // chips it has in flight — untracked until adoption, so a cancel must destroy them itself

        private void Awake()
        {
            int n = anchorsBySeat != null ? anchorsBySeat.Length : 0;
            _stacks = new List<GameObject>[n * MaxHands];
            _lastAmount = new long[n * MaxHands];
            for (int i = 0; i < _stacks.Length; i++) { _stacks[i] = new List<GameObject>(); _lastAmount[i] = -1; }
            _lastHandCount = new int[n];
            for (int i = 0; i < n; i++) _lastHandCount[i] = -1;
        }

        private void OnEnable()
        {
            if (builder == null) builder = FindAnyObjectByType<BetBuilder>(FindObjectsInactive.Include);
            EnsureBetSpots();
            if (builder != null)
            {
                builder.OnDealCommitted += OnDealCommitted;
                // Dropping a chip is a purely LOCAL action — no board push follows it — so without this the bet
                // amount wouldn't appear until the next snapshot arrived. This makes the label track the pile live.
                builder.OnBetChanged += OnLocalBetChanged;
                builder.OnBetRejected += OnBetRejected;
            }
            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
            if (builder != null)
            {
                builder.OnDealCommitted -= OnDealCommitted;
                builder.OnBetChanged -= OnLocalBetChanged;
                builder.OnBetRejected -= OnBetRejected;
            }
            // A disable stops the gather coroutine; don't leave the seat skipped — or a dead routine/chip list held — on re-enable.
            _gatheringSeat = 0;
            _gatherRoutine = null;
            _gatherChips = null;
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (anchorsBySeat == null || _stacks == null) return;

            bool inRound = board != null && board.RoundInProgress;
            // Also draw a seat's wager DURING the betting window if it has ACTIVELY bet this window (BetThisWindow) —
            // so a deal held for other players keeps chips on the felt, and everyone sees placed bets stack up. A
            // persisted auto-repeat with no fresh bet stays hidden until re-confirmed.
            bool windowOpen = board != null && !inRound && board.BettingExpiresAt.HasValue;
            bool render = inRound || windowOpen;

            // Round-end HOLD: with a settle director registered, DON'T clear the committed stacks at settle. Latch on
            // the in-round → settle transition and ignore every push until the director calls ReleaseHold — so the
            // stacks survive on the felt for it to fly (losers → dealer, winnings → seats) and then sweep. With NO
            // director this whole block is inert and the stacks clear at settle exactly as before.
            if (_held) return;
            if (_settleDirector != null && _prevInRound && !inRound)
            {
                _held = true;
                _prevInRound = false;
                return;
            }
            // Was the round ALREADY running on the previous push? Chips appearing on the very first in-round push are
            // the bets that were placed during the window (or a mid-round join seeing the felt for the first time) —
            // not chips being added. Only a seat that grows while we were already watching is a double or a split.
            bool wasInRound = _prevInRound;
            _prevInRound = inRound;
            if (render && chipSet != null) { _lastMinBet = board.MinBet; _lastMaxBet = board.MaxBet; }   // cache stakes for BuildLooseStack

            IReadOnlyList<long> values = (render && chipSet != null) ? chipSet.Values(board.MinBet, board.MaxBet) : null;
            // PrefabsFor, not LevelPrefabs: aligned 1:1 with `values`. The denomination window slides up the ladder on
            // richer tables, so the i-th value is not generally the i-th colour rank.
            IReadOnlyList<GameObject> prefabs = chipSet != null ? chipSet.PrefabsFor(board.MinBet, board.MaxBet) : null;

            for (int i = 0; i < anchorsBySeat.Length; i++)
            {
                if (i + 1 == _gatheringSeat) continue;   // a deal gather owns this seat's chips right now — don't build/clear over it

                var player = SeatPlayer(board, i + 1);
                // While a round RUNS, only a seat that is actually IN it has money on the felt. `InRound` is the
                // server's own per-seat flag, set inside the deal alongside the stake debit, so it can never disagree
                // with what was wagered.
                //
                // Without it, a seat that holds a bet it never got to play showed chips through a round it wasn't in:
                // join mid-round, press DEAL as the betting window closes, and the server rejects the bet ("cannot
                // change bets during an active round") — but the chips have already been gathered onto the felt
                // locally, and the amount badge with them. The player then sits behind a wager, watching a round he is
                // not in, with the "waiting for the next round" panel up. Same guard covers a remote seat that sat out.
                bool showRound = inRound && player != null && player.InRound;                 // round running → all committed hands
                bool showWindow = windowOpen && player != null && player.BetThisWindow;       // window → just this window's single bet
                var hands = (showRound || showWindow) ? player.Hands : null;
                int handCount = (showRound && hands != null && hands.Count > 0) ? hands.Count : 1;  // window shows only hand 0
                bool handCountChanged = handCount != _lastHandCount[i];   // split happened → offsets shift, rebuild
                _lastHandCount[i] = handCount;

                long seatTotal = 0;
                for (int h = 0; h < MaxHands; h++)
                {
                    int slot = i * MaxHands + h;
                    long amount = 0;
                    if (hands != null && hands.Count > 0)
                    {
                        if (showRound && h < hands.Count) amount = (long)hands[h].Bet;
                        else if (showWindow && h == 0) amount = (long)hands[0].Bet;   // between rounds: the single window bet only
                    }
                    seatTotal += amount;

                    if (!handCountChanged && amount == _lastAmount[slot]) continue;   // unchanged → leave as-is (no flicker)
                    long previous = _lastAmount[slot];
                    _lastAmount[slot] = amount;

                    if (amount > 0 && values != null && values.Count > 0)
                        Build(slot, i, HandOffset(i + 1, h, handCount), amount, values, prefabs);
                    else
                        Clear(slot);

                    // MORE chips on a live hand: a DOUBLE (this slot's stake grows) or a SPLIT (the second hand's slot
                    // gets its matching bet from nothing). Both are the player putting money down mid-round and both
                    // want the same sound. `previous` clamped at 0 so a slot's first-ever build counts as growth;
                    // wasInRound is what keeps the opening deal and a mid-round join from firing it for every seat.
                    if (inRound && wasInRound && amount > System.Math.Max(0L, previous))
                        ChipsAdded?.Invoke(i + 1, ChipWorldPoint(i + 1, h, handCount));
                }

                // While the LOCAL player is still dropping chips, the board has no bet yet — PlaceBet isn't sent until
                // Deal — so seatTotal is 0 and the label would stay blank until the round started. Show the builder's
                // running total instead: it is exactly what is physically sitting on the bet spot right now, so the
                // amount appears with the first chip and tracks every one after it. Once the bet is committed the
                // builder clears and the board total (above) takes over seamlessly.
                long shown = seatTotal;
                if (builder != null && i + 1 == LocalSeat())
                {
                    long pending = (long)builder.Total;
                    if (pending > 0) shown = pending;
                }

                SetLabel(i, shown > 0 ? ChipView.Format(shown) : string.Empty);
            }
        }

        /// <summary>
        /// Chips were ADDED to a hand that is already in play — a double, or the matching bet on a freshly split hand.
        /// Carries the seat and the world point of that hand's stack. Not raised for the opening bets, which arrive
        /// with the round rather than during it.
        /// </summary>
        public event System.Action<int, Vector3> ChipsAdded;

        // World point of one hand's stack, for callers that only have the seat/hand. Safe on an unauthored seat.
        private Vector3 ChipWorldPoint(int seatNumber, int handIndex, int handCount)
        {
            var anchor = ChipAnchor(seatNumber);
            return anchor != null ? anchor.TransformPoint(HandOffset(seatNumber, handIndex, handCount)) : transform.position;
        }

        /// <summary>
        /// Keep every committed stack sitting under its hand. The tuck flips BETWEEN board pushes (it waits for the
        /// last card to land, then a delay), and OnBoard only rebuilds a stack when its AMOUNT changes — so nothing
        /// here can be driven off the board. Easing toward the target every frame also gives the move for free, so the
        /// chips travel with the cards instead of snapping once the rebuild happens to fire.
        ///
        /// Deliberately skipped while a gather owns the seat (those chips are mid-flight under their own animation) and
        /// while the round-end director holds the felt (it detaches the stacks it is flying, and the ones it leaves
        /// should stay put).
        /// </summary>
        private void Update()
        {
            if (!followTuck || _held || tableView == null) return;
            if (_stacks == null || anchorsBySeat == null || _lastHandCount == null) return;

            // Frame-rate independent ease. tuckFollowSeconds is the visual settle time, so the time constant is a
            // fraction of it (an exponential is asymptotic — it never formally arrives).
            float k = tuckFollowSeconds > 0.001f
                ? 1f - Mathf.Exp(-Time.unscaledDeltaTime / (tuckFollowSeconds * 0.35f))
                : 1f;

            for (int i = 0; i < anchorsBySeat.Length; i++)
            {
                if (i + 1 == _gatheringSeat) continue;
                int handCount = Mathf.Max(1, _lastHandCount[i]);

                for (int h = 0; h < MaxHands; h++)
                {
                    int slot = i * MaxHands + h;
                    if (slot >= _stacks.Length) continue;
                    var list = _stacks[slot];
                    if (list == null || list.Count == 0) continue;

                    bool tucked = tableView.IsHandTucked(i + 1, h, handCount);
                    float shrink = tucked ? Mathf.Max(0.1f, 1f - tuckChipShrink) : 1f;
                    Vector3 baseOffset = HandOffset(i + 1, h, handCount);
                    Vector3 targetScale = Vector3.one * (FeltChipScale * shrink);
                    float step = stackStep * shrink;   // a shorter chip needs a shorter climb, or the stack stretches

                    for (int c = 0; c < list.Count; c++)
                    {
                        if (list[c] == null) continue;
                        var t = list[c].transform;
                        t.localPosition = Vector3.Lerp(t.localPosition, baseOffset + new Vector3(0f, step * c, 0f), k);
                        t.localScale = Vector3.Lerp(t.localScale, targetScale, k);
                    }
                }
            }
        }

        /// <summary>
        /// This seat's split offset for <paramref name="handIndex"/>, expressed in the seat's BET-anchor frame.
        ///
        /// The offset comes from the view (<see cref="BlackjackTableView.HandCenterLocal"/>) in the seat's CARD-anchor
        /// frame, but the stacks are parented to a SEPARATE bet anchor which may be oriented differently (the card
        /// anchors are turned to face the player). Applying the card-frame offset straight to the bet anchor MIRRORED
        /// it whenever the two differ by ~180°, so hand 0's chips sat under hand 1's cards and vice versa — most
        /// visible when doubling one hand of a split, where the growing stack appears under the other hand. Converting
        /// through world space makes each stack sit under its own hand however either anchor is authored.
        /// </summary>
        private Vector3 HandOffset(int seatNumber, int handIndex, int handCount)
        {
            if (tableView == null) return Vector3.zero;
            // HandCenterDrawn, not HandCenterLocal: a tucked hand has been pulled in toward the seat, and its chips
            // belong under its cards. This is also what HandChipPoint is built on, so the round-end director collects
            // from and pays to wherever the stack actually ended up — the two can't drift apart.
            Vector3 local = followTuck
                ? tableView.HandCenterDrawn(seatNumber, handIndex, handCount)
                : tableView.HandCenterLocal(handIndex, handCount);
            // A tucked hand's stack gets its own extra shift, in the BET anchor's own frame. The tuck pulls the CARDS
            // in, and the chips inherit that — but the distance from a hand to its chips is authored as the gap between
            // the two anchors, and that gap was set for a full-size hand. Once the hand shrinks it reads as too much
            // empty felt, and no amount of pull-in closes it because both ends move together.
            Vector3 tuckShift = followTuck && tableView.IsHandTucked(seatNumber, handIndex, handCount)
                ? tuckChipNudge
                : Vector3.zero;

            if (local == Vector3.zero) return tuckShift;               // single hand sits on the anchor — nothing to convert

            var cardAnchor = tableView.SeatAnchor(seatNumber);
            int idx = seatNumber - 1;
            var betAnchor = (anchorsBySeat != null && idx >= 0 && idx < anchorsBySeat.Length) ? anchorsBySeat[idx] : null;
            if (cardAnchor == null || betAnchor == null) return local + tuckShift;   // not authored → previous behaviour

            // Direction-only conversion (TransformVector, not TransformPoint): honours rotation + scale, ignores
            // the anchors' differing positions, which is exactly what a per-hand offset needs. The tuck shift is added
            // AFTER, because it is authored in the bet anchor's frame — it is about the chips, not about the hand.
            return betAnchor.InverseTransformVector(cardAnchor.TransformVector(local)) + tuckShift;
        }

        // The seat's player (holds Hands + BetThisWindow), or null if the seat is empty.
        private static PlayerView SeatPlayer(BoardSnapshot board, int seatNumber)
        {
            if (board?.Seats == null) return null;
            foreach (var s in board.Seats)
                if (s.SeatNumber == seatNumber) return s.Player;
            return null;
        }

        private void Build(int slot, int seatIdx, Vector3 baseOffset, long amount,
                           IReadOnlyList<long> values, IReadOnlyList<GameObject> prefabs)
        {
            Clear(slot);
            var anchor = anchorsBySeat[seatIdx];
            if (anchor == null) return;
            SpawnStack(anchor, baseOffset, amount, values, prefabs, _stacks[slot]);
        }

        // Shared stack builder: decompose `amount` greedily largest-denomination-first (capped at maxChips), spawn the
        // chips under `parent` climbing +Y by stackStep with colliders OFF (visual only), and make the TOP chip show the
        // stack TOTAL. Used for the committed bet stacks (into _stacks) AND the director's unmanaged winnings stacks.
        private void SpawnStack(Transform parent, Vector3 baseLocalPos, long amount,
                                IReadOnlyList<long> values, IReadOnlyList<GameObject> prefabs, List<GameObject> outList)
        {
            if (parent == null || prefabs == null || values == null || outList == null) return;

            long remaining = amount;
            int placed = 0;
            for (int vi = values.Count - 1; vi >= 0 && placed < maxChips; vi--)
            {
                long v = values[vi];
                var prefab = vi < prefabs.Count ? prefabs[vi] : null;
                if (prefab == null || v <= 0) continue;
                while (remaining >= v && placed < maxChips)
                {
                    var go = Instantiate(prefab, parent);
                    go.transform.localPosition = baseLocalPos + new Vector3(0f, stackStep * placed, 0f);
                    go.transform.localRotation = Quaternion.identity;
                    // Same felt size as a hand-dropped chip, so a chip doesn't change size when the dropped pile is
                    // gathered into the committed stack, or when winnings land next to it.
                    go.transform.localScale = Vector3.one * FeltChipScale;
                    foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false; // visual only
                    var chip = go.GetComponentInChildren<ChipView>();
                    if (chip != null) chip.SetValue(v);
                    outList.Add(go);
                    remaining -= v;
                    placed++;
                }
            }

            // The TOP chip (last placed, the visible one) shows the stack's TOTAL value instead of its own denomination.
            if (outList.Count > 0)
            {
                var topChip = outList[outList.Count - 1].GetComponentInChildren<ChipView>();
                if (topChip != null) topChip.SetValue(amount);
            }
        }

        private void Clear(int slot)
        {
            if (_stacks == null || slot >= _stacks.Length) return;
            var list = _stacks[slot];
            for (int j = 0; j < list.Count; j++) if (list[j] != null) Destroy(list[j]);
            list.Clear();
        }

        /// <summary>Felt chip size, from the shared <see cref="ChipSet"/> — the one value every felt spawner uses.
        /// <see cref="BetSpot"/> reads it through here too, so a dropped chip and a stacked one always match.</summary>
        public float FeltChipScale => chipSet != null ? chipSet.FeltChipScale : 1f;

        /// <summary>This client's seat (1-based), or -1 when not seated. Only that seat has chips being dropped, so
        /// only its label reads the builder's pending total.</summary>
        private int LocalSeat() => table != null ? table.MySeat : -1;

        /// <summary>
        /// A chip was dropped on (or cleared from) our bet spot. Repaint just OUR label — deliberately not a full
        /// Render: the physical chips are already on the spot under the player's control, and rebuilding the stacks
        /// here would destroy and respawn them mid-interaction.
        ///
        /// Falls back to the board's committed total when the pending pile is empty, so clearing a bet doesn't blank
        /// a label that should still be showing an already-placed wager.
        /// </summary>
        private void OnLocalBetChanged(decimal pending)
        {
            int seat = LocalSeat();
            if (seat <= 0) return;
            int idx = seat - 1;
            if (labelsBySeat == null || idx < 0 || idx >= labelsBySeat.Length) return;

            long shown = (long)pending;
            if (shown <= 0) shown = CommittedTotal(seat);
            SetLabel(idx, shown > 0 ? ChipView.Format(shown) : string.Empty);
        }

        /// <summary>What the BOARD says this seat has wagered across its hands (0 if none / not seated).</summary>
        private long CommittedTotal(int seatNumber)
        {
            var player = SeatPlayer(table != null ? table.Board : null, seatNumber);
            if (player?.Hands == null) return 0;
            long total = 0;
            for (int h = 0; h < player.Hands.Count; h++) total += (long)player.Hands[h].Bet;
            return total;
        }

        // Empty text = nothing on the felt, so the whole badge rolls away rather than just blanking its text (which
        // would leave the pill's background sitting there).
        private void SetLabel(int seatIdx, string text)
        {
            if (labelsBySeat == null || seatIdx < 0 || seatIdx >= labelsBySeat.Length) return;
            var badge = labelsBySeat[seatIdx];
            if (badge == null) return;

            if (string.IsNullOrEmpty(text)) badge.Hide();
            else badge.Show(text);
        }

        // ---- Gather-on-deal: pull the dropped pile into the committed stack (driven by BetBuilder.OnDealCommitted) ----

        private void EnsureBetSpots()
        {
            int n = anchorsBySeat != null ? anchorsBySeat.Length : 0;
            if (n == 0) return;
            var found = new BetSpot[n];
            if (betSpotsBySeat != null)
                for (int i = 0; i < betSpotsBySeat.Length && i < n; i++) found[i] = betSpotsBySeat[i];
            foreach (var bs in FindObjectsByType<BetSpot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                int idx = bs.SeatNumber - 1;
                if (idx >= 0 && idx < n && found[idx] == null) found[idx] = bs;
            }
            betSpotsBySeat = found;
        }

        // The local player just dealt: pull THEIR dropped chips from the bet spot into their stack anchor, then adopt
        // them as the committed stack so the board render leaves them in place. Only the local seat has dropped chips on
        // this client, so this is local-only — every other seat's bet renders straight from the board exactly as before.
        private void OnDealCommitted(long amount)
        {
            if (table == null || betSpotsBySeat == null || _gatheringSeat != 0) return;
            int seat = table.MySeat;
            int idx = seat - 1;
            if (idx < 0 || idx >= betSpotsBySeat.Length) return;
            var spot = betSpotsBySeat[idx];
            if (spot == null || !spot.HasChips) return;   // nothing dropped (e.g. window auto-deal) → normal render

            var anchor = (anchorsBySeat != null && idx < anchorsBySeat.Length) ? anchorsBySeat[idx] : null;
            var chips = spot.DetachChips();
            if (anchor == null || chips.Count == 0)
            {
                for (int i = 0; i < chips.Count; i++) if (chips[i] != null) Destroy(chips[i]);
                return;
            }
            _gatheringSeat = seat;
            _gatherChips = chips;
            _gatherRoutine = StartCoroutine(GatherRoutine(idx, anchor, chips, amount));
        }

        /// <summary>
        /// The server refused the bet we already gathered onto the felt. Take those chips straight back off — stack,
        /// amount badge and the sentinels — so the seat reads as unbet immediately rather than carrying a wager it
        /// does not hold. Cancels a gather still in flight; those chips are destroyed with the stack.
        /// </summary>
        private void OnBetRejected(long amount)
        {
            int seat = LocalSeat();
            int seatIdx = seat - 1;
            if (_stacks == null || seatIdx < 0 || _lastHandCount == null || seatIdx >= _lastHandCount.Length) return;

            // A gather mid-flight owns this seat and would otherwise adopt the chips AFTER we cleared them. Stop just
            // that routine — not every coroutine on this component, which would strand a round-end float-away at
            // scale 0 instead of destroying its chips. Its in-flight PullChips exit on their own once the chips are
            // destroyed below (they null-check every frame).
            if (_gatheringSeat == seat)
            {
                if (_gatherRoutine != null) { StopCoroutine(_gatherRoutine); _gatherRoutine = null; }
                if (_gatherChips != null)
                {
                    // Not in _stacks yet (adoption is the LAST thing the gather does), so Clear below would miss them.
                    for (int i = 0; i < _gatherChips.Count; i++)
                        if (_gatherChips[i] != null) Destroy(_gatherChips[i]);
                    _gatherChips = null;
                }
                _gatheringSeat = 0;
            }

            for (int h = 0; h < MaxHands; h++)
            {
                int slot = seatIdx * MaxHands + h;
                if (slot < 0 || slot >= _stacks.Length) continue;
                Clear(slot);
                _lastAmount[slot] = -1;   // sentinel must reset or the same amount won't rebuild when it IS accepted
            }
            _lastHandCount[seatIdx] = -1;
            SetLabel(seatIdx, string.Empty);
        }

        private IEnumerator GatherRoutine(int seatIdx, Transform anchor, List<GameObject> chips, long amount)
        {
            Vector3 baseOffset = HandOffset(seatIdx + 1, 0, 1);   // a fresh deal always starts with one hand
            int inFlight = 0;
            for (int i = 0; i < chips.Count; i++)
            {
                if (chips[i] == null) continue;
                Vector3 targetLocal = baseOffset + new Vector3(0f, stackStep * i, 0f);
                inFlight++;
                StartCoroutine(PullChip(chips[i], anchor, targetLocal, () => inFlight--));   // fly one after another
                if (gatherInterval > 0f) yield return new WaitForSeconds(gatherInterval);
            }
            while (inFlight > 0) yield return null;

            AdoptAsStack(seatIdx, chips, amount);
            _gatheringSeat = 0;
            _gatherRoutine = null;
            _gatherChips = null;
        }

        // Tween one chip from where it landed to its slot in the stack: a smooth pull with a small mid-flight arc, then
        // park it exactly on the anchor so it's indistinguishable from a spawned stack chip.
        private IEnumerator PullChip(GameObject chip, Transform anchor, Vector3 targetLocal, System.Action done)
        {
            if (chip == null) { done?.Invoke(); yield break; }
            Vector3 startPos = chip.transform.position;
            Quaternion startRot = chip.transform.rotation;
            Vector3 endPos = anchor.TransformPoint(targetLocal);
            Quaternion endRot = anchor.rotation;

            float t = 0f;
            while (t < gatherDuration && gatherDuration > 0f && chip != null)
            {
                t += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, t / gatherDuration);
                Vector3 p = Vector3.LerpUnclamped(startPos, endPos, k);
                p.y += Mathf.Sin(k * Mathf.PI) * gatherArc;   // lift up then settle — reads as a magnetic pull
                chip.transform.position = p;
                chip.transform.rotation = Quaternion.Slerp(startRot, endRot, k);
                yield return null;
            }
            if (chip != null)
            {
                chip.transform.SetParent(anchor, worldPositionStays: false);
                chip.transform.localPosition = targetLocal;
                chip.transform.localRotation = Quaternion.identity;
            }
            done?.Invoke();
        }

        // Register the gathered chips AS this seat's committed (hand-0) stack, so the authoritative board render sees the
        // amount already displayed and leaves it untouched (no rebuild flicker). Settle later flies these exactly like a
        // spawned stack. Physics is stripped so they behave as pure visuals.
        private void AdoptAsStack(int seatIdx, List<GameObject> chips, long amount)
        {
            int slot = seatIdx * MaxHands + 0;
            if (_stacks == null || slot < 0 || slot >= _stacks.Length)
            {
                for (int i = 0; i < chips.Count; i++) if (chips[i] != null) Destroy(chips[i]);
                return;
            }
            Clear(slot);   // normally empty (we skipped this seat while gathering); guards against a slipped-in build
            var list = _stacks[slot];
            for (int i = 0; i < chips.Count; i++)
            {
                var chip = chips[i];
                if (chip == null) continue;
                var rb = chip.GetComponent<Rigidbody>();
                if (rb != null) Destroy(rb);
                foreach (var c in chip.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                list.Add(chip);
            }
            if (list.Count > 0)
            {
                var top = list[list.Count - 1].GetComponentInChildren<ChipView>();
                if (top != null) top.SetValue(amount);   // top chip shows the stack total, matching SpawnStack
            }
            _lastAmount[slot] = amount;                    // so the next board render leaves the adopted stack as-is
            if (seatIdx < _lastHandCount.Length) _lastHandCount[seatIdx] = 1;
            SetLabel(seatIdx, amount > 0 ? ChipView.Format(amount) : string.Empty);
        }

        // ---- Round-end director hooks (ONLY a RoundEndDirector calls these; see that class) ----

        /// <summary>
        /// Register the round-end director. While one is registered, committed stacks are HELD at settle (not cleared)
        /// so the director can fly losers to the dealer and pay winners, then <see cref="ReleaseHold"/>. Idempotent.
        /// </summary>
        public void RegisterSettleDirector(MonoBehaviour director)
        {
            if (director != null) _settleDirector = director;
        }

        /// <summary>Clear the director (disabled/destroyed). If it goes away mid-hold, release so the stacks don't freeze on the felt.</summary>
        public void UnregisterSettleDirector(MonoBehaviour director)
        {
            if (_settleDirector != director) return;
            _settleDirector = null;
            if (_held) ReleaseHold();
        }

        /// <summary>
        /// Hand the director BOTH hand slots' committed chips for a seat (split-aware) WITHOUT destroying them, and
        /// forget them here — reset the sentinels so the same recurring bet rebuilds cleanly next round. The director
        /// flies these to the dealer and destroys them. Returns an empty list for an unknown / unstacked seat.
        /// </summary>
        public IReadOnlyList<GameObject> DetachSeatStacks(int seatNumber)
        {
            var outList = new List<GameObject>();
            for (int h = 0; h < MaxHands; h++) CollectHandStack(seatNumber, h, outList);
            int seatIdx = seatNumber - 1;
            if (_lastHandCount != null && seatIdx >= 0 && seatIdx < _lastHandCount.Length) _lastHandCount[seatIdx] = -1;
            return outList;
        }

        /// <summary>
        /// Hand the director ONE hand's committed chips (split-aware) without destroying them, and forget them here.
        /// Used so a split's LOSING hand is collected on its own while the other hand keeps its stack — collecting the
        /// whole seat would sweep a winning hand's bet to the dealer too ("one loss takes all").
        /// </summary>
        public IReadOnlyList<GameObject> DetachHandStack(int seatNumber, int handIndex)
        {
            var outList = new List<GameObject>();
            CollectHandStack(seatNumber, handIndex, outList);
            // NOTE: _lastHandCount is deliberately NOT reset here — the seat's other hand may still be on the felt and
            // a hand-count change forces a full rebuild of both slots, which would resurrect the stack we just took.
            return outList;
        }

        // Move one slot's chips into `outList` and forget them (shared by the per-hand and whole-seat detach).
        private void CollectHandStack(int seatNumber, int handIndex, List<GameObject> outList)
        {
            int seatIdx = seatNumber - 1;
            if (_stacks == null || anchorsBySeat == null || seatIdx < 0 || seatIdx >= anchorsBySeat.Length) return;
            if (handIndex < 0 || handIndex >= MaxHands) return;
            int slot = seatIdx * MaxHands + handIndex;
            if (slot < 0 || slot >= _stacks.Length) return;
            var list = _stacks[slot];
            for (int j = 0; j < list.Count; j++) if (list[j] != null) outList.Add(list[j]);
            list.Clear();
            _lastAmount[slot] = -1;   // sentinel MUST reset or the same recurring bet won't rebuild next round
        }

        /// <summary>
        /// FLOAT AWAY: this seat's remaining committed chips peel off the TOP of the stack one after another, each
        /// rising (ease-out) and shrinking away. Used as the hand-off into the win burst — the felt stack lifts off a
        /// beat before the burst fires, so the chips flying to the balance read as THIS stack leaving, instead of the
        /// stack sitting there untouched while a second set of chips appears out of nowhere.
        ///
        /// Effect length is <c>Rise Seconds + Stagger x (chips - 1)</c>: every chip gets the full rise time, so adding
        /// chips makes the peel longer rather than making each chip's motion shorter (and choppier).
        ///
        /// The chips are detached from tracking first, so the board render and <see cref="ReleaseHold"/> won't fight
        /// the animation or double-destroy them. Only whatever is still on the felt moves — on a split whose losing
        /// hand was already collected, that is exactly the winning hand's stack.
        ///
        /// Chips SHRINK as they rise rather than fading: the chip materials are opaque (URP Lit), so an alpha fade
        /// would need a transparent material variant per chip and would silently do nothing on the standard ones.
        /// </summary>
        /// <returns>How long the shrink takes, so the caller can fire the burst right after it.</returns>
        public float PlayFloatAway(int seatNumber)
        {
            var detached = DetachSeatStacks(seatNumber);
            if (detached == null || detached.Count == 0) return 0f;

            var chips = new List<Transform>(detached.Count);
            for (int i = 0; i < detached.Count; i++)
                if (detached[i] != null) chips.Add(detached[i].transform);
            if (chips.Count == 0) return 0f;

            float duration = Mathf.Max(0.01f, floatAwayShrinkSeconds);
            StartCoroutine(ShrinkAway(chips, duration));
            return duration;
        }

        // All chips shrink to nothing TOGETHER over `duration` — one coroutine for the whole stack, so the effect
        // length never depends on the chip count.
        private IEnumerator ShrinkAway(List<Transform> chips, float duration)
        {
            var baseScales = new Vector3[chips.Count];
            for (int i = 0; i < chips.Count; i++)
                if (chips[i] != null) baseScales[i] = chips[i].localScale;

            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float k = 1f - Mathf.Clamp01(t / duration);
                for (int i = 0; i < chips.Count; i++)
                    if (chips[i] != null) chips[i].localScale = baseScales[i] * k;
                yield return null;
            }

            for (int i = 0; i < chips.Count; i++)
                if (chips[i] != null) Destroy(chips[i].gameObject);
        }

        /// <summary>
        /// WORLD position of a seat's per-hand chip spot — the point a split hand's chips are collected FROM and paid
        /// TO, matching where <see cref="Render"/> builds that hand's stack. Falls back to the seat anchor's own
        /// position for an unauthored seat. <paramref name="handCount"/> is how many hands that seat played this round
        /// (the split offset is centred, so it depends on the count).
        /// </summary>
        public Vector3 HandChipPoint(int seatNumber, int handIndex, int handCount)
        {
            var anchor = ChipAnchor(seatNumber);
            if (anchor == null) return Vector3.zero;
            return anchor.TransformPoint(HandOffset(seatNumber, handIndex, handCount));
        }

        /// <summary>
        /// Build an UNMANAGED winnings stack (never tracked in <c>_stacks</c>, so the board render never touches it)
        /// parented to <paramref name="parent"/> (the dealer's hand). The director flies it to the winner and destroys
        /// it. <paramref name="amount"/> is the seat's net winnings (LastResults.Delta). Empty for a non-positive
        /// amount or a missing chip set. Uses the stakes cached from the last live board so the denominations match.
        /// </summary>
        public IReadOnlyList<GameObject> BuildLooseStack(Transform parent, Vector3 localPos, long amount)
        {
            var outList = new List<GameObject>();
            if (parent == null || amount <= 0 || chipSet == null) return outList;
            SpawnStack(parent, localPos, amount, chipSet.Values(_lastMinBet, _lastMaxBet),
                       chipSet.PrefabsFor(_lastMinBet, _lastMaxBet), outList);
            return outList;
        }

        /// <summary>That seat's chip anchor (1-based), bounds-checked; null if unauthored — the director pays winnings here.</summary>
        public Transform ChipAnchor(int seatNumber)
        {
            int idx = seatNumber - 1;
            if (anchorsBySeat == null || idx < 0 || idx >= anchorsBySeat.Length) return null;
            return anchorsBySeat[idx];
        }

        /// <summary>
        /// End the hold: destroy any stacks still on the felt (winners' returned bets, pushes that never moved), reset
        /// every sentinel so the next round rebuilds cleanly, and resume normal board-driven rendering.
        /// </summary>
        public void ReleaseHold()
        {
            _held = false;
            if (_stacks != null)
                for (int slot = 0; slot < _stacks.Length; slot++) { Clear(slot); _lastAmount[slot] = -1; }
            if (_lastHandCount != null)
                for (int i = 0; i < _lastHandCount.Length; i++) _lastHandCount[i] = -1;
        }
    }
}
