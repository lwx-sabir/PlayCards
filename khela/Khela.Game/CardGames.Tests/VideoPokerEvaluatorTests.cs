using System.Collections.Generic;
using System.Linq;
using CardGames.Platforms;
using CardGames.VideoPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks the video-poker 5-card evaluator: category detection with Royal Flush as its OWN top category, the
    /// normal ranking (FLUSH &gt; STRAIGHT), the Ace-low wheel, the pair/quad + kicker fields the paytable reads, and —
    /// the acid test — the exact canonical hand frequencies over all 2,598,960 five-card hands.
    /// </summary>
    public class VideoPokerEvaluatorTests
    {
        private static Card C(FaceValue f, Suit s) => new Card(s, f, true);
        private static List<Card> H(params Card[] cs) => cs.ToList();
        private static VideoPokerHandRank Eval(List<Card> h) => VideoPokerEvaluator.Evaluate(h);
        private static VideoPokerCategory Cat(List<Card> h) => Eval(h).Category;

        // ---- category detection ----
        [Fact] public void RoyalFlush_IsItsOwnCategory() => Assert.Equal(VideoPokerCategory.RoyalFlush, Cat(H(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades))));

        [Fact] public void StraightFlush_NotRoyal() => Assert.Equal(VideoPokerCategory.StraightFlush, Cat(H(
            C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Eight, Suit.Hearts), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Six, Suit.Hearts), C(FaceValue.Five, Suit.Hearts))));

        [Fact] public void Wheel_StraightFlush_IsStraightFlush_NotRoyal() => Assert.Equal(VideoPokerCategory.StraightFlush, Cat(H(
            C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Two, Suit.Clubs), C(FaceValue.Three, Suit.Clubs), C(FaceValue.Four, Suit.Clubs), C(FaceValue.Five, Suit.Clubs))));

        [Fact] public void Quads_CarryRankAndKicker()
        {
            var r = Eval(H(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Ace, Suit.Diamonds), C(FaceValue.Three, Suit.Spades)));
            Assert.Equal(VideoPokerCategory.FourOfAKind, r.Category);
            Assert.Equal(14, r.PrimaryRank);   // quad Aces — for DDB tiers
            Assert.Equal(3, r.Kicker);         // the 2/3/4 kicker DDB reads
        }

        [Fact] public void FullHouse() => Assert.Equal(VideoPokerCategory.FullHouse, Cat(H(
            C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Queen, Suit.Spades))));

        [Fact] public void Flush() => Assert.Equal(VideoPokerCategory.Flush, Cat(H(
            C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Clubs), C(FaceValue.Two, Suit.Clubs))));

        [Fact] public void Straight() => Assert.Equal(VideoPokerCategory.Straight, Cat(H(
            C(FaceValue.Nine, Suit.Spades), C(FaceValue.Eight, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Six, Suit.Diamonds), C(FaceValue.Five, Suit.Spades))));

        [Fact] public void Wheel_A2345_IsStraight() => Assert.Equal(VideoPokerCategory.Straight, Cat(H(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.Two, Suit.Hearts), C(FaceValue.Three, Suit.Clubs), C(FaceValue.Four, Suit.Diamonds), C(FaceValue.Five, Suit.Spades))));

        [Fact] public void Trips() => Assert.Equal(VideoPokerCategory.ThreeOfAKind, Cat(H(
            C(FaceValue.Seven, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Two, Suit.Spades))));

        [Fact] public void TwoPair() => Assert.Equal(VideoPokerCategory.TwoPair, Cat(H(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Two, Suit.Spades))));

        [Fact] public void Pair_CarriesRank_ForJacksOrBetterGate()
        {
            var jacks = Eval(H(C(FaceValue.Jack, Suit.Spades), C(FaceValue.Jack, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Nine, Suit.Spades)));
            Assert.Equal(VideoPokerCategory.Pair, jacks.Category); Assert.Equal(11, jacks.PrimaryRank);
            var tens = Eval(H(C(FaceValue.Ten, Suit.Spades), C(FaceValue.Ten, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Nine, Suit.Spades)));
            Assert.Equal(VideoPokerCategory.Pair, tens.Category); Assert.Equal(10, tens.PrimaryRank);
        }

        [Fact] public void HighCard() => Assert.Equal(VideoPokerCategory.HighCard, Cat(H(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Diamonds), C(FaceValue.Two, Suit.Spades))));

        // ---- standard ranking: FLUSH beats STRAIGHT ----
        [Fact]
        public void Flush_Beats_Straight()
        {
            var flush = Eval(H(C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Clubs), C(FaceValue.Two, Suit.Clubs)));
            var straight = Eval(H(C(FaceValue.Nine, Suit.Spades), C(FaceValue.Eight, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Six, Suit.Diamonds), C(FaceValue.Five, Suit.Spades)));
            Assert.True(flush.CompareTo(straight) > 0);
        }

        // ---- the acid test: exact canonical frequencies over all C(52,5) = 2,598,960 hands, royal split out ----
        [Fact]
        public void CategoryFrequencies_MatchCanonicalPokerCounts()
        {
            var deck = new Deck().Cards;   // 52 distinct
            var buf = new Card[5];
            var cnt = new long[12];        // HighCard..RoyalFlush (FiveOfAKind/WildRoyal unreachable without wilds)
            int n = deck.Count;
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                    for (int c = b + 1; c < n; c++)
                        for (int d = c + 1; d < n; d++)
                            for (int e = d + 1; e < n; e++)
                            {
                                buf[0] = deck[a]; buf[1] = deck[b]; buf[2] = deck[c]; buf[3] = deck[d]; buf[4] = deck[e];
                                cnt[(int)VideoPokerEvaluator.Evaluate(buf).Category]++;
                            }

            Assert.Equal(4,        cnt[(int)VideoPokerCategory.RoyalFlush]);
            Assert.Equal(36,       cnt[(int)VideoPokerCategory.StraightFlush]);   // 40 straight-flushes − 4 royals
            Assert.Equal(624,      cnt[(int)VideoPokerCategory.FourOfAKind]);
            Assert.Equal(3744,     cnt[(int)VideoPokerCategory.FullHouse]);
            Assert.Equal(5108,     cnt[(int)VideoPokerCategory.Flush]);
            Assert.Equal(10200,    cnt[(int)VideoPokerCategory.Straight]);
            Assert.Equal(54912,    cnt[(int)VideoPokerCategory.ThreeOfAKind]);
            Assert.Equal(123552,   cnt[(int)VideoPokerCategory.TwoPair]);
            Assert.Equal(1098240,  cnt[(int)VideoPokerCategory.Pair]);
            Assert.Equal(1302540,  cnt[(int)VideoPokerCategory.HighCard]);
            Assert.Equal(2598960,  cnt.Sum());
        }
    }
}
