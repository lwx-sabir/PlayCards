using System.Linq;
using CardGames.Blackjack.CardGames.Blackjack;
using CardGames.Platforms;

namespace Khela.Game.Managers
{
    /// <summary>
    /// Single source of truth for the client-facing board snapshot. Masks the dealer hole card,
    /// reports only the dealer's visible value, and publishes the provably-fair commitment (never
    /// the secret server seed). Used by the hub (join/request), live broadcasts, and REST reads.
    /// </summary>
    public static class BlackjackBoard
    {
        public static object Build(BlackjackTable table) => new
        {
            table.TableId,
            table.MaxPlayers,
            table.MaxSeatsPerUser,
            table.RoundInProgress,
            table.CurrentSeatNumber,
            table.CurrentHandIndex,
            table.TurnExpiresAt,
            table.LastHandId, // id of the most recently settled hand — feeds GET /verify/{handId}
            table.LastResults, // per-seat outcome of the last settled round (drives the client result banner)
            // Commitment only — the server seed stays secret until reveal/verify.
            Fairness = new { table.ServerSeedHash, table.ClientSeed, table.RoundNonce },
            Dealer = new
            {
                Cards = table.Game.Dealer.Hand.Cards.Select(MaskCard),
                HandValue = table.Game.Dealer.Hand.GetVisibleSum()
            },
            Players = table.Game.Players.Select(ToPlayerDto),
            Seats = table.Seats.Select(ToSeatDto)
        };

        // Face-down cards (the dealer hole card) are masked so a snapshot never leaks the down card.
        private static object MaskCard(Card c) => c.IsCardUp
            ? new { FaceVal = (int)c.FaceVal, Suit = (int)c.Suit, Value = CardValue(c), c.IsCardUp }
            : new { FaceVal = 0, Suit = 0, Value = 0, c.IsCardUp };

        // Blackjack point value of a card (J/Q/K = 10, Ace = 11; the hand total resolves soft/hard aces).
        private static int CardValue(Card c) => c.FaceVal switch
        {
            FaceValue.Ace => 11,
            FaceValue.Jack or FaceValue.Queen or FaceValue.King => 10,
            _ => (int)c.FaceVal
        };

        private static object ToPlayerDto(Player p) => new
        {
            p.Id,
            p.Name,
            p.Balance,
            p.SeatNumber,
            Hands = p.Hands.Select((h, idx) => new
            {
                HandIndex = idx,
                h.Bet,
                Insurance = h.InsuranceBet,
                Cards = h.Hand.Cards.Select(c => new { FaceVal = (int)c.FaceVal, Suit = (int)c.Suit, Value = CardValue(c), c.IsCardUp }),
                HandValue = h.Hand.GetSumOfHand()
            }),
            p.Wins,
            p.Losses,
            p.Push
        };

        private static object ToSeatDto(Seat s) => new
        {
            s.SeatNumber,
            Occupied = s.Player != null,
            Player = s.Player == null ? null : ToPlayerDto(s.Player)
        };
    }
}
