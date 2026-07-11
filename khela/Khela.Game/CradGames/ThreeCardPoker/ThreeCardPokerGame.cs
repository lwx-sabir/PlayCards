using System.Collections.Generic;
using System.Linq;
using CardGames.Platforms;

namespace CardGames.ThreeCardPoker
{
    /// <summary>One seat in a Three Card Poker round: its 3 cards + its Play/Fold decision.</summary>
    public sealed class ThreeCardSeat
    {
        public int SeatNumber { get; }
        public List<Card> Cards { get; } = new(3);
        public bool Played { get; set; }   // false = folded (the safe default / timeout action)
        public ThreeCardSeat(int seatNumber) => SeatNumber = seatNumber;
    }

    /// <summary>
    /// In-memory Three Card Poker round state + the deal. A SINGLE 52-card deck is reshuffled every hand
    /// (no carryover, no counting). Deal 3 to each seat + 3 to the dealer from the provably-fair shuffle; the
    /// dealer's 3 cards are part of the committed deck (revealed, not drawn, after seats decide). Pure engine —
    /// no wallet/table/Redis. Settlement is <see cref="ThreeCardPokerSettlement"/>.
    /// </summary>
    public sealed class ThreeCardPokerGame
    {
        public Deck Deck { get; private set; }
        public List<Card> DealerCards { get; } = new(3);
        public List<ThreeCardSeat> Seats { get; private set; } = new();

        /// <summary>Deal a fresh round for <paramref name="seatCount"/> seats. With a seed the shuffle is
        /// deterministic and provably-fair (replayable); without one it uses a crypto shuffle.</summary>
        public void DealNewGame(int seatCount, byte[] seed = null)
        {
            Deck = new Deck();                       // single 52-card deck
            if (seed != null) Deck.Shuffle(seed); else Deck.Shuffle();

            DealerCards.Clear();
            Seats = Enumerable.Range(0, seatCount).Select(i => new ThreeCardSeat(i)).ToList();

            for (int c = 0; c < 3; c++)              // round-robin: one card to each seat, then the dealer, ×3
            {
                foreach (var s in Seats) s.Cards.Add(Deck.Draw());
                DealerCards.Add(Deck.Draw());
            }
        }

        /// <summary>SHA-256 of the committed deck order — the provably-fair audit fingerprint.</summary>
        public string DeckHash() => Deck?.ComputeHash();
    }
}
