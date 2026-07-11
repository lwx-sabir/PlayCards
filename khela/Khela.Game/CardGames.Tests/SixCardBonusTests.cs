using System.Collections.Generic;
using System.Linq;
using CardGames.Platforms;
using CardGames.ThreeCardPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>The 6-Card Bonus in settlement: best 5 of player's 3 + dealer's 3, pays on trips+, pays even on
    /// a fold, and the royal top tier — all via the config paytable (WoO Version 1-A defaults).</summary>
    public class SixCardBonusTests
    {
        private static Card C(FaceValue f, Suit s) => new Card(s, f, true);
        private static List<Card> H(params Card[] cs) => cs.ToList();
        private static readonly ThreeCardPokerPaytables PT = ThreeCardPokerPaytables.Default;

        private static decimal SixCard(List<Card> player, List<Card> dealer, bool played = true)
            => ThreeCardPokerSettlement.Settle(player, dealer,
                   new ThreeCardPokerBets { Ante = 10m, SixCard = 5m }, played, PT).SixCardReturn;

        [Fact]
        public void SixCard_Flush_Pays()
        {
            var p = H(C(FaceValue.Two, Suit.Hearts), C(FaceValue.Five, Suit.Hearts), C(FaceValue.Nine, Suit.Hearts));
            var d = H(C(FaceValue.King, Suit.Hearts), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Three, Suit.Spades));
            Assert.Equal(5m * (15 + 1), SixCard(p, d));   // best-5 = five hearts → flush 15:1
        }

        [Fact]
        public void SixCard_Trips_Pays()
        {
            var p = H(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.Two, Suit.Clubs));
            var d = H(C(FaceValue.King, Suit.Diamonds), C(FaceValue.Five, Suit.Spades), C(FaceValue.Nine, Suit.Hearts));
            Assert.Equal(5m * (7 + 1), SixCard(p, d));    // three kings → trips 7:1
        }

        [Fact]
        public void SixCard_BelowTrips_Loses()
        {
            var p = H(C(FaceValue.Two, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Nine, Suit.Clubs));
            var d = H(C(FaceValue.King, Suit.Diamonds), C(FaceValue.Four, Suit.Spades), C(FaceValue.Jack, Suit.Hearts));
            Assert.Equal(0m, SixCard(p, d));              // best is high card → no pay
        }

        [Fact]
        public void SixCard_RoyalFlush_PaysTopTier()
        {
            var p = H(C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades));
            var d = H(C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades), C(FaceValue.Three, Suit.Hearts));
            Assert.Equal(5m * (1000 + 1), SixCard(p, d)); // A-K-Q-J-10 spades → royal 1000:1
        }

        [Fact]
        public void SixCard_PaysOnFold()
        {
            var p = H(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.Two, Suit.Clubs));
            var d = H(C(FaceValue.King, Suit.Diamonds), C(FaceValue.Five, Suit.Spades), C(FaceValue.Nine, Suit.Hearts));
            Assert.Equal(5m * (7 + 1), SixCard(p, d, played: false)); // side bet pays even when folded
        }
    }
}
