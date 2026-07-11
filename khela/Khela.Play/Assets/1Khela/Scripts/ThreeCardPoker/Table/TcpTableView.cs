using System.Collections;
using System.Collections.Generic;
using PlayCard.Game.Cards;
using PlayCard.ThreeCardPoker.Dtos;
using UnityEngine;

namespace PlayCard.ThreeCardPoker.Table
{
    /// <summary>
    /// Renders a Three Card Poker board from a server <see cref="TcpBoard"/>: each occupied seat's 3-card hand plus
    /// the dealer's hand. DIFFS against the previous render — reusing unchanged cards, only renting / updating /
    /// recycling what changed — so repeated identical pushes (every SignalR tick + every poll) don't flicker.
    /// Simpler than blackjack: exactly one hand per seat, no splits, no per-card hit/stand.
    ///
    /// The dealer's real cards arrive only at reveal (settle); during the acting phase the dealer shows three
    /// FACE-DOWN backs (a pure client visual — the server never sends the hidden cards). Cards lay out as a FAN
    /// via <see cref="CardLocalTRS"/>. <see cref="TcpTableController"/> calls <see cref="Render"/> on every board.
    /// </summary>
    public sealed class TcpTableView : MonoBehaviour
    {
        [Header("Card")]
        [Tooltip("Prefab whose ROOT has a CardVisual (the same wired card prefab blackjack uses).")]
        [SerializeField] private CardVisual cardPrefab;
        [Tooltip("Card art applied to every spawned card. Swap to reskin the whole table.")]
        [SerializeField] private CardSkin skin;

        [Header("Anchors")]
        [Tooltip("Where the dealer's hand is laid out.")]
        [SerializeField] private Transform dealerAnchor;
        [Tooltip("One anchor per seat. Element 0 = seat 1, element 1 = seat 2, and so on.")]
        [SerializeField] private Transform[] seatAnchors;

        [Header("Hand fan — DEFAULT (every seat + dealer unless overridden below)")]
        [SerializeField] private Vector2 cardGap = new Vector2(0.25f, 0f);
        [SerializeField] private float rotationPerCard = 8f;
        [SerializeField] private float cardLift = 0.004f;
        [Tooltip("Which side the fan opens toward. The newest/last card is ALWAYS on top — this only chooses the side.")]
        [SerializeField] private bool mirrorFan = false;

        [System.Serializable]
        public struct SeatFan
        {
            [Tooltip("Tick to give THIS seat its own gap/rotation (for its camera angle); untick to use the default.")]
            public bool overrideDefault;
            public Vector2 cardGap;
            public float rotationPerCard;
            public float cardLift;
            public bool mirrorFan;
        }

        [Header("Per-seat overrides (optional — for each seat's camera angle)")]
        [Tooltip("Element 0 = seat 1, … Tick a seat's Override Default to give it its own fan; else it uses the default. The dealer always uses the default.")]
        [SerializeField] private SeatFan[] seatFanOverrides;

        [Header("Dealer")]
        [Tooltip("Show three face-down dealer backs during the acting phase (a pure visual; the server sends the real cards only at reveal).")]
        [SerializeField] private bool showDealerBacksDuringActing = true;

        [Header("Deal / collect animation (optional — leave points empty to snap)")]
        [Tooltip("Cards SLIDE IN from here when dealt (the shoe). Empty = cards appear in place.")]
        [SerializeField] private Transform dealSource;
        [Tooltip("Cards SLIDE OUT to here when the round ends. Empty = cards just clear.")]
        [SerializeField] private Transform discardTarget;
        [SerializeField] private float dealSeconds = 0.35f;
        [SerializeField] private float recenterSeconds = 0.18f;
        [SerializeField] private float collectSeconds = 0.3f;
        [Range(0f, 1f)] [SerializeField] private float collectShrink = 0.2f;
        [SerializeField] private float dealStagger = 0.1f;
        [SerializeField] private float collectStagger = 0.1f;

        private CardPool _pool;
        private Vector3 _cardBaseScale = Vector3.one;

        private readonly Dictionary<int, Slot> _rendered = new Dictionary<int, Slot>();
        private readonly HashSet<int> _desired = new HashSet<int>();
        private readonly List<int> _stale = new List<int>();
        private readonly HashSet<int> _pendingDeal = new HashSet<int>();
        private readonly List<NewCard> _newThisPass = new List<NewCard>();

        private struct Slot { public CardVisual Card; public CardId Data; }
        private struct NewCard { public int Key; public CardMover Mover; public Vector3 Pos; public Quaternion Rot; public Vector3 Scale; public int Order; }

        // A stable placeholder for a face-down dealer back (rank/suit are irrelevant — CardVisual draws the back).
        private static readonly CardId DealerBack = new CardId(CardRank.Ace, CardSuit.Spades, false);

        private void Awake()
        {
            _pool = new CardPool(cardPrefab, transform);
            if (cardPrefab != null) _cardBaseScale = cardPrefab.transform.localScale;
        }

        /// <summary>Anchor-local transform for card <paramref name="cardIndex"/> of a <paramref name="cardCount"/>-card
        /// hand at <paramref name="seat"/> (1-based; 0 = dealer). Cards fan about the anchor: each steps sideways by
        /// Card Gap, tilts by Rotation Per Card, and lifts so the newest sits on top.</summary>
        public void CardLocalTRS(int seat, int cardIndex, int cardCount, out Vector3 pos, out Quaternion rot)
        {
            ResolveFan(seat, out Vector2 gap, out float anglePer, out float lift, out bool mirror);
            float k = cardIndex - (cardCount - 1) * 0.5f;   // centred index
            float s = (mirror ? -1f : 1f) * k;
            rot = Quaternion.Euler(0f, s * anglePer, 0f);
            pos = new Vector3(s * gap.x, lift * cardIndex, s * gap.y);   // LIFT always by deal order → newest on top
        }

        /// <summary>The world anchor for a seat (1-based) or the dealer (0). Null if not authored.</summary>
        public Transform SeatAnchor(int seat) => seat <= 0 ? dealerAnchor : AnchorForSeat(seat);

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
        public void Render(TcpBoard board)
        {
            if (board == null || cardPrefab == null || _pool == null) return;

            _desired.Clear();

            // Dealer: real cards once revealed, else three face-down backs during acting.
            if (dealerAnchor != null)
            {
                if (board.Dealer != null && board.Dealer.Count > 0)
                {
                    var dealer = new List<CardId>(board.Dealer.Count);
                    foreach (var c in board.Dealer) dealer.Add(c.ToCardId(true));
                    LayOutHand(dealer, dealerAnchor, 0);
                }
                else if (showDealerBacksDuringActing && board.Phase == "acting")
                {
                    LayOutHand(new List<CardId> { DealerBack, DealerBack, DealerBack }, dealerAnchor, 0);
                }
            }

            // Seats: each occupied seat's dealt hand (present during acting + complete; empty during betting).
            if (board.Seats != null)
            {
                foreach (var seat in board.Seats)
                {
                    if (seat == null || !seat.Occupied || seat.Cards == null || seat.Cards.Count == 0) continue;
                    var anchor = AnchorForSeat(seat.SeatNumber);
                    if (anchor == null) continue;   // server seat beyond our authored anchors — skip
                    var cards = new List<CardId>(seat.Cards.Count);
                    foreach (var c in seat.Cards) cards.Add(c.ToCardId(true));
                    LayOutHand(cards, anchor, seat.SeatNumber);
                }
            }

            // Deal this pass's new cards in ONE BY ONE, in real dealing order (sorted, then staggered).
            if (_newThisPass.Count > 0)
            {
                _newThisPass.Sort((a, b) => a.Order.CompareTo(b.Order));
                for (int i = 0; i < _newThisPass.Count; i++)
                    StartCoroutine(DealRoutine(_newThisPass[i], i * Mathf.Max(0f, dealStagger)));
                _newThisPass.Clear();
            }

            // Anything no longer on the board leaves — collected per seat, seats staggered.
            _stale.Clear();
            foreach (var kv in _rendered)
                if (!_desired.Contains(kv.Key)) _stale.Add(kv.Key);
            if (_stale.Count > 0) CollectStale();
        }

        private void LayOutHand(List<CardId> cards, Transform anchor, int seat)
        {
            int count = cards.Count;
            for (int i = 0; i < count; i++)
            {
                int key = SlotKey(seat, i);
                _desired.Add(key);
                var data = cards[i];
                CardLocalTRS(seat, i, count, out var pos, out var rot);
                var targetScale = _cardBaseScale;

                if (_rendered.TryGetValue(key, out var slot))
                {
                    if (_pendingDeal.Contains(key))
                    {
                        if (!SameCard(slot.Data, data)) { slot.Card.SetCard(data); slot.Data = data; _rendered[key] = slot; }
                    }
                    else
                    {
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

                    if (dealSource != null && dealSeconds > 0f && dealStagger > 0f)
                    {
                        mover.Snap(anchor.InverseTransformPoint(dealSource.position), rot, targetScale);
                        card.gameObject.SetActive(false);
                        _pendingDeal.Add(key);
                        _newThisPass.Add(new NewCard { Key = key, Mover = mover, Pos = pos, Rot = rot, Scale = targetScale, Order = DealOrder(seat, i) });
                    }
                    else if (dealSource != null && dealSeconds > 0f)
                    {
                        mover.Snap(anchor.InverseTransformPoint(dealSource.position), rot, targetScale);
                        mover.Target(pos, rot, targetScale, dealSeconds);
                    }
                    else
                    {
                        mover.Snap(pos, rot, targetScale);
                    }
                }
            }
        }

        private static CardMover EnsureMover(CardVisual card)
        {
            var m = card.GetComponent<CardMover>();
            return m != null ? m : card.gameObject.AddComponent<CardMover>();
        }

        // Real dealing order: card by card (cardIndex), players (seat asc) then the dealer (seat 0) LAST.
        private static int DealOrder(int seat, int cardIndex) => cardIndex * 1000 + (seat == 0 ? 999 : seat);

        private IEnumerator DealRoutine(NewCard nc, float delay)
        {
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
            if (nc.Mover == null || !_rendered.ContainsKey(nc.Key)) yield break;
            _pendingDeal.Remove(nc.Key);
            nc.Mover.gameObject.SetActive(true);
            nc.Mover.Target(nc.Pos, nc.Rot, nc.Scale, dealSeconds);
        }

        private void CollectStale()
        {
            var groups = new Dictionary<int, List<CardVisual>>();
            for (int i = 0; i < _stale.Count; i++)
            {
                int key = _stale[i];
                var card = _rendered[key].Card;
                _rendered.Remove(key);
                _pendingDeal.Remove(key);
                int seat = key / 100;   // SlotKey packs seat*100 + cardIndex
                if (!groups.TryGetValue(seat, out var list)) { list = new List<CardVisual>(); groups[seat] = list; }
                list.Add(card);
            }

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
            if (!card.gameObject.activeSelf) { _pool.Release(card); yield break; }
            var tr = card.transform;
            var parent = tr.parent;
            Vector3 local = parent != null ? parent.InverseTransformPoint(discardTarget.position) : tr.localPosition;
            EnsureMover(card).Target(local, tr.localRotation, tr.localScale * (1f - collectShrink), collectSeconds);
            yield return new WaitForSecondsRealtime(collectSeconds);
            _pool.Release(card);
        }

        private Transform AnchorForSeat(int seatNumber)
        {
            int idx = seatNumber - 1;
            if (seatAnchors == null || idx < 0 || idx >= seatAnchors.Length) return null;
            return seatAnchors[idx];
        }

        // dealer = seat 0; pack (seat, cardIndex) into one int (cards per hand stay well under 100).
        private static int SlotKey(int seat, int cardIndex) => seat * 100 + cardIndex;

        private static bool SameCard(CardId a, CardId b) => a.Rank == b.Rank && a.Suit == b.Suit && a.FaceUp == b.FaceUp;
    }
}
