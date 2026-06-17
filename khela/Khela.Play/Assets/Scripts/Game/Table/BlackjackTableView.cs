using System.Collections.Generic;
using UnityEngine;
using PlayCard.Game.Cards;
using PlayCard.Game.Dtos;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Renders a whole blackjack board from a server <see cref="BoardSnapshot"/>: the dealer's hand
    /// plus every occupied seat (split hands included), reusing a <see cref="CardPool"/>.
    /// The client only lays out what the server pushed — it never decides cards, and a face-down
    /// dealer hole card draws as a back because <see cref="CardVisual"/> honours <c>IsCardUp</c>.
    ///
    /// Wire it to live pushes with <see cref="Bind"/>, or drive it directly with <see cref="Render"/>.
    /// </summary>
    public sealed class BlackjackTableView : MonoBehaviour
    {
        [Header("Card")]
        [Tooltip("Prefab whose ROOT has a CardVisual (the wired Card.prefab).")]
        [SerializeField] private CardVisual cardPrefab;
        [Tooltip("Card art applied to every spawned card. Swap to reskin the whole table.")]
        [SerializeField] private CardSkin skin;

        [Header("Anchors")]
        [Tooltip("Where the dealer's hand is laid out.")]
        [SerializeField] private Transform dealerAnchor;
        [Tooltip("One anchor per seat. Element 0 = seat 1, element 1 = seat 2, and so on.")]
        [SerializeField] private Transform[] seatAnchors;

        [Header("Layout (local to each anchor)")]
        [Tooltip("Offset between consecutive cards within one hand.")]
        [SerializeField] private Vector3 cardStep = new Vector3(0.35f, 0f, -0.05f);
        [Tooltip("Offset per split-hand index, so a player's 2nd/3rd hands don't overlap.")]
        [SerializeField] private Vector3 splitHandStep = new Vector3(0f, -0.6f, 0f);

        private CardPool _pool;
        private IBlackjackHubClient _hub;

        private void Awake() => _pool = new CardPool(cardPrefab, transform);

        /// <summary>Subscribe to a hub so every server push redraws the board automatically.</summary>
        public void Bind(IBlackjackHubClient hub)
        {
            if (_hub != null) _hub.OnTableUpdated -= Render;
            _hub = hub;
            if (_hub != null) _hub.OnTableUpdated += Render;
        }

        private void OnDestroy()
        {
            if (_hub != null) _hub.OnTableUpdated -= Render;
        }

        /// <summary>Lay out an entire board snapshot. Safe to call on every "TableUpdated".</summary>
        public void Render(BoardSnapshot board)
        {
            if (board == null || cardPrefab == null || _pool == null) return;

            _pool.Recycle();

            if (board.Dealer != null && dealerAnchor != null)
                LayOutHand(board.Dealer.Cards, dealerAnchor, 0);

            if (board.Seats == null) return;
            foreach (var seat in board.Seats)
            {
                if (seat?.Player == null) continue;
                var anchor = AnchorForSeat(seat.SeatNumber);
                if (anchor == null) continue;

                var hands = seat.Player.Hands;
                for (int h = 0; h < hands.Count; h++)
                    LayOutHand(hands[h].Cards, anchor, h);
            }
        }

        private void LayOutHand(List<CardView> cards, Transform anchor, int handIndex)
        {
            if (cards == null) return;
            Vector3 handOrigin = splitHandStep * handIndex;
            for (int i = 0; i < cards.Count; i++)
            {
                var card = _pool.Rent(anchor);
                card.transform.localPosition = handOrigin + cardStep * i;
                card.transform.localRotation = Quaternion.identity;
                if (skin != null) card.Skin = skin;       // else keep the prefab's own skin
                card.SetCard(cards[i]); // hole card (IsCardUp == false) → renders the back
            }
        }

        private Transform AnchorForSeat(int seatNumber)
        {
            int idx = seatNumber - 1;
            if (seatAnchors == null || idx < 0 || idx >= seatAnchors.Length) return null;
            return seatAnchors[idx];
        }
    }
}
