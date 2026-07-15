using System.Collections;
using System.Collections.Generic;
using Animancer;
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

        [Header("Timing")]
        [Tooltip("Playback speed of the throw clips. >1 if the pack's deal is too slow to keep up with dealing.")]
        [SerializeField] private float dealSpeed = 1f;
        [Tooltip("Extra pause AFTER each card is released, before the next throw starts. 0 = deal the next as soon as this one is thrown.")]
        [SerializeField] private float perCardStagger = 0.12f;
        [Tooltip("Safety: if a throw clip's Throw event never fires within this many seconds, release its card anyway so dealing can't stall. Only bites a clip missing its release event.")]
        [SerializeField] private float releaseTimeout = 4f;

        [Header("In-hand card prop (optional)")]
        [Tooltip("A FACE-DOWN card parented under her hand bone and positioned in her grip (you author its transform in " +
                 "the scene). Bind ShowInHandCard/HideInHandCard to the clip's Pick/Throw events. Empty = none.")]
        [SerializeField] private GameObject inHandCard;

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

        private void Awake()
        {
            if (animancer == null) animancer = GetComponentInChildren<AnimancerComponent>(true);
            if (table == null) table = FindAnyObjectByType<TableController>();
            if (tableView == null) tableView = FindAnyObjectByType<BlackjackTableView>();
            HideProp();
        }

        private void OnEnable()
        {
            if (table != null) table.OnBoardChanged += OnBoard;
            else Debug.LogWarning("[DealerAnimator] no TableController found — dealer won't animate to the board.");
            if (tableView != null) tableView.RegisterConductor(this);   // become the deal conductor: cards fly on our throws
            PlayIdle();
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
            if (tableView != null) tableView.UnregisterConductor(this);   // gone → the view reverts to its own staggered deal
            if (_pump != null) { StopCoroutine(_pump); _pump = null; }
            _pumping = false;
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (board == null) return;
            // Reset the baseline to EMPTY between rounds, in lockstep with BlackjackTableView.Render, which only lays
            // cards out while RoundInProgress (and clears the felt otherwise). The server leaves the settled hands on the
            // board until the next deal, so counting them would keep a stale high baseline and the next round's fresh
            // 2-card hands (2 < prevTotal) would emit ZERO throws → the view's parked cards would never release.
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
                    _newThisBoard.Sort((a, b) => a.order.CompareTo(b.order));   // players in seat order, dealer last
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
        private static int DealOrder(int seat, int cardIndex) => cardIndex * 1000 + (seat == 0 ? 999 : seat);

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

            var clip = ClipFor(seat);
            if (animancer == null || clip == null || clip.Clip == null)
            {
                ReleaseCurrent();   // no clip → deal it now (never stalls)
                yield break;
            }

            var state = animancer.Play(clip);              // the clip's own Pick/Throw events (set on its preview) fire
            state.Speed = Mathf.Max(0.01f, dealSpeed);
            // NOTE: do NOT touch state.Events here — it can clobber the clip's serialized Show/Throw events. Idle is
            // returned to by Pump when the queue drains.

            // Hold until the Throw event releases THIS card (or the safety timeout) — the next throw only starts after
            // this one has actually thrown, so cards can't stampede out ahead of the animation.
            float t = 0f;
            while (!_released && t < releaseTimeout) { t += Time.unscaledDeltaTime; yield return null; }
            if (!_released) ReleaseCurrent();
        }

        private void PlayIdle()
        {
            HideProp();   // hide the cosmetic card ONLY — never release here (that's the throw event's job)
            if (animancer != null && idle != null && idle.Clip != null) animancer.Play(idle);
        }

        // Hide the cosmetic in-hand prop WITHOUT releasing a card. Used by idle/awake; the throw event uses
        // HideInHandCard (below) which also releases.
        private void HideProp() { if (inHandCard != null) inHandCard.SetActive(false); }

        // Release exactly ONE parked card to the current throw's seat, at most once per throw. Fired at the clip's
        // release event (via HideInHandCard), and as a safety-net for a missing clip/event so a card can't stall.
        private void ReleaseCurrent()
        {
            if (_released || _currentThrowSeat < 0) return;   // -1 sentinel = idle/awake; seat 0 (dealer) IS valid
            _released = true;
            if (tableView != null) tableView.ReleaseNextCard(_currentThrowSeat);
        }

        // --- Bind these to each throw clip's Pick / Throw events in the Animancer inspector (public for that reason). ---
        public void ShowInHandCard() { if (inHandCard != null) inHandCard.SetActive(true); }

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
