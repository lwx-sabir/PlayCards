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
    /// Card layout fans by <see cref="cardStep"/> but compresses to fit <see cref="maxHandWidth"/>, so a
    /// 2-card and a 7-card hand both stay on the felt. <see cref="TableController"/> calls <see cref="Render"/>
    /// on every board (push or inline action) — the view does NOT subscribe to the hub itself.
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

        [Header("Layout (local to each anchor)")]
        [Tooltip("Offset between consecutive cards within one hand (full spread).")]
        [SerializeField] private Vector3 cardStep = new Vector3(0.35f, 0f, -0.05f);
        [Tooltip("Max width a hand may span (anchor-local X). More cards fan tighter to stay within this — so " +
                 "2, 5 and 7-card hands all fit. 0 = no limit (always full spread).")]
        [SerializeField] private float maxHandWidth = 1.6f;
        [Tooltip("Offset per split-hand index, so a player's 2nd/3rd hands don't overlap.")]
        [SerializeField] private Vector3 splitHandStep = new Vector3(0f, -0.6f, 0f);

        private CardPool _pool;

        private readonly Dictionary<int, Slot> _rendered = new Dictionary<int, Slot>();
        private readonly HashSet<int> _desired = new HashSet<int>();
        private readonly List<int> _stale = new List<int>();

        private struct Slot { public CardVisual Card; public CardView Data; }

        private void Awake() => _pool = new CardPool(cardPrefab, transform);

        /// <summary>
        /// Anchor-local position for card <paramref name="cardIndex"/> of a <paramref name="cardCount"/>-card
        /// hand. Cards spread by <see cref="cardStep"/> but the whole hand compresses uniformly to fit
        /// <see cref="maxHandWidth"/>. Shared by the runtime layout AND the anchor gizmo, so the editor
        /// preview matches exactly what gets dealt.
        /// </summary>
        public Vector3 CardLocalPos(int handIndex, int cardIndex, int cardCount)
        {
            Vector3 step = cardStep;
            if (cardCount > 1 && maxHandWidth > 0f)
            {
                float spread = Mathf.Abs(cardStep.x) * (cardCount - 1);
                if (spread > maxHandWidth) step *= maxHandWidth / spread; // compress to fit the width
            }
            return splitHandStep * handIndex + step * cardIndex;
        }

        /// <summary>Lay out a board snapshot, diffing against the last render. Safe to call on every push.</summary>
        public void Render(BoardSnapshot board)
        {
            if (board == null || cardPrefab == null || _pool == null) return;

            _desired.Clear();

            if (board.Dealer != null && dealerAnchor != null)
                LayOutHand(board.Dealer.Cards, dealerAnchor, 0, 0);

            if (board.Seats != null)
            {
                foreach (var seat in board.Seats)
                {
                    if (seat?.Player == null) continue;
                    var anchor = AnchorForSeat(seat.SeatNumber);
                    if (anchor == null) continue;   // server seat beyond our authored anchors (e.g. 4–5) — skip

                    var hands = seat.Player.Hands;
                    for (int h = 0; h < hands.Count; h++)
                        LayOutHand(hands[h].Cards, anchor, seat.SeatNumber, h);
                }
            }

            _stale.Clear();
            foreach (var kv in _rendered)
                if (!_desired.Contains(kv.Key)) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++)
            {
                _pool.Release(_rendered[_stale[i]].Card);
                _rendered.Remove(_stale[i]);
            }
        }

        private void LayOutHand(List<CardView> cards, Transform anchor, int seat, int handIndex)
        {
            if (cards == null) return;
            int count = cards.Count;

            for (int i = 0; i < count; i++)
            {
                int key = SlotKey(seat, handIndex, i);
                _desired.Add(key);
                var data = cards[i];
                Vector3 pos = CardLocalPos(handIndex, i, count);

                if (_rendered.TryGetValue(key, out var slot))
                {
                    slot.Card.transform.localPosition = pos;
                    if (!SameCard(slot.Data, data))   // only re-render when the card actually changed (incl. reveal)
                    {
                        slot.Card.SetCard(data);
                        slot.Data = data;
                        _rendered[key] = slot;
                    }
                }
                else
                {
                    var card = _pool.Rent(anchor);
                    card.transform.localPosition = pos;
                    card.transform.localRotation = Quaternion.identity;
                    if (skin != null) card.Skin = skin;
                    card.SetCard(data);
                    _rendered[key] = new Slot { Card = card, Data = data };
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
    }
}
