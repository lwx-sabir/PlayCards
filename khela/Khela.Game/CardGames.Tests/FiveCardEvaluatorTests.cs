using System.Collections.Generic;
using System.Linq;
using CardGames.Platforms;
using CardGames.ThreeCardPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks the standard 5-card evaluator used by the 6-Card Bonus: category detection, the normal ranking
    /// (FLUSH &gt; STRAIGHT), the Ace-low A-5-4-3-2 wheel, royal detection, best-of-6 selection, and — the acid
    /// test — the exact canonical hand frequencies over all 2,598,960 five-card hands.
    /// </summary>
    public class FiveCardEvaluatorTests
    {
        private static Card C(FaceValue f, Suit s) => new Card(s, f, true);
        private static List<Card> H(params Card[] cs) => cs.ToList();
        private static FiveCardCategory Cat(List<Card> h) => FiveCardEvaluator.Evaluate(h).Category;

        // ---- category detection ----
        [Fact] public void Royal() { var r = FiveCardEvaluator.Evaluate(H(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades)));
            Assert.Equal(FiveCardCategory.StraightFlush, r.Category); Assert.True(r.IsRoyal); }

        [Fact] public void StraightFlush_NotRoyal() { var r = FiveCardEvaluator.Evaluate(H(
            C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Eight, Suit.Hearts), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Six, Suit.Hearts), C(FaceValue.Five, Suit.Hearts)));
            Assert.Equal(FiveCardCategory.StraightFlush, r.Category); Assert.False(r.IsRoyal); }

        [Fact] public void Quads() => Assert.Equal(FiveCardCategory.FourOfAKind, Cat(H(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Ace, Suit.Diamonds), C(FaceValue.King, Suit.Spades))));

        [Fact] public void FullHouse() => Assert.Equal(FiveCardCategory.FullHouse, Cat(H(
            C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Queen, Suit.Spades))));

        [Fact] public void Flush() => Assert.Equal(FiveCardCategory.Flush, Cat(H(
            C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Clubs), C(FaceValue.Two, Suit.Clubs))));

        [Fact] public void Straight() => Assert.Equal(FiveCardCategory.Straight, Cat(H(
            C(FaceValue.Nine, Suit.Spades), C(FaceValue.Eight, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Six, Suit.Diamonds), C(FaceValue.Five, Suit.Spades))));

        [Fact] public void Wheel_A2345_IsStraight() => Assert.Equal(FiveCardCategory.Straight, Cat(H(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.Two, Suit.Hearts), C(FaceValue.Three, Suit.Clubs), C(FaceValue.Four, Suit.Diamonds), C(FaceValue.Five, Suit.Spades))));

        [Fact] public void Trips() => Assert.Equal(FiveCardCategory.ThreeOfAKind, Cat(H(
            C(FaceValue.Seven, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Two, Suit.Spades))));

        [Fact] public void TwoPair() => Assert.Equal(FiveCardCategory.TwoPair, Cat(H(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Two, Suit.Spades))));

        [Fact] public void Pair() => Assert.Equal(FiveCardCategory.Pair, Cat(H(
            C(FaceValue.Five, Suit.Spades), C(FaceValue.Five, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Nine, Suit.Spades))));

        [Fact] public void HighCard() => Assert.Equal(FiveCardCategory.HighCard, Cat(H(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Diamonds), C(FaceValue.Two, Suit.Spades))));

        // ---- standard ranking: FLUSH beats STRAIGHT (opposite of the 3-card game) ----
        [Fact]
        public void Flush_Beats_Straight()
        {
            var flush = FiveCardEvaluator.Evaluate(H(C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Clubs), C(FaceValue.Two, Suit.Clubs)));
            var straight = FiveCardEvaluator.Evaluate(H(C(FaceValue.Nine, Suit.Spades), C(FaceValue.Eight, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Six, Suit.Diamonds), C(FaceValue.Five, Suit.Spades)));
            Assert.True(flush.CompareTo(straight) > 0);
        }

        // ---- best of 6 ----
        [Fact]
        public void BestOfSix_PicksTheFlush()
        {
            var six = H(C(FaceValue.King, Suit.Hearts), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Seven, Suit.Hearts),
                        C(FaceValue.Four, Suit.Hearts), C(FaceValue.Two, Suit.Hearts), C(FaceValue.Ace, Suit.Spades));
            Assert.Equal(FiveCardCategory.Flush, FiveCardEvaluator.BestOfSix(six).Category);
        }

        [Fact]
        public void BestOfSix_PicksTheFullHouse()
        {
            var six = H(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Clubs),
                        C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Two, Suit.Hearts));
            Assert.Equal(FiveCardCategory.FullHouse, FiveCardEvaluator.BestOfSix(six).Category);
        }

        // ---- the acid test: exact canonical 5-card frequencies over all C(52,5) = 2,598,960 hands ----
        [Fact]
        public void CategoryFrequencies_MatchCanonicalPokerCounts()
        {
            var deck = new Deck().Cards;   // 52 distinct
            var buf = new Card[5];
            var cnt = new long[9];
            long royal = 0;
            int n = deck.Count;
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                    for (int c = b + 1; c < n; c++)
                        for (int d = c + 1; d < n; d++)
                            for (int e = d + 1; e < n; e++)
                            {
                                buf[0] = deck[a]; buf[1] = deck[b]; buf[2] = deck[c]; buf[3] = deck[d]; buf[4] = deck[e];
                                var r = FiveCardEvaluator.Evaluate(buf);
                                cnt[(int)r.Category]++;
                                if (r.IsRoyal) royal++;
                            }

            Assert.Equal(40,       cnt[(int)FiveCardCategory.StraightFlush]);
            Assert.Equal(624,      cnt[(int)FiveCardCategory.FourOfAKind]);
            Assert.Equal(3744,     cnt[(int)FiveCardCategory.FullHouse]);
            Assert.Equal(5108,     cnt[(int)FiveCardCategory.Flush]);
            Assert.Equal(10200,    cnt[(int)FiveCardCategory.Straight]);
            Assert.Equal(54912,    cnt[(int)FiveCardCategory.ThreeOfAKind]);
            Assert.Equal(123552,   cnt[(int)FiveCardCategory.TwoPair]);
            Assert.Equal(1098240,  cnt[(int)FiveCardCategory.Pair]);
            Assert.Equal(1302540,  cnt[(int)FiveCardCategory.HighCard]);
            Assert.Equal(4,        royal);
            Assert.Equal(2598960,  cnt.Sum());
        }
    }
}
