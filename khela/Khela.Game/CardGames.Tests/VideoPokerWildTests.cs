using System;
using System.Collections.Generic;
using System.Linq;
using CardGames.Platforms;
using CardGames.VideoPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks the WILD evaluator (Deuces Wild — the four 2s are always wild) and the full-pay Deuces paytable. The
    /// evaluator is brute-force (tries every substitution) so it is correct by construction; these tests pin the
    /// category NAMES + the tricky distinctions: natural royal (no deuce) vs Wild Royal (deuce-completed, pays far
    /// less), Five of a Kind, the Four Deuces bonus, and deuces completing quads/flush/straight/trips.
    /// </summary>
    public class VideoPokerWildTests
    {
        private static Card C(FaceValue f, Suit s) => new Card(s, f, true);
        private static readonly Func<Card, bool> Deuce = c => c.FaceVal == FaceValue.Two;
        private static VideoPokerHandRank W(params Card[] cs) => VideoPokerEvaluator.EvaluateWild(cs.ToList(), Deuce);
        private static VideoPokerCategory Cat(params Card[] cs) => W(cs).Category;
        private static readonly VideoPokerPaytable DW = VideoPokerPaytable.DeucesWildFullPay;

        // ---- category detection (Deuces) ----

        [Fact] public void NaturalRoyal_NoDeuce_IsRoyalFlush_NotWild() => Assert.Equal(VideoPokerCategory.RoyalFlush, Cat(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades)));

        [Fact] public void DeuceCompletesRoyal_IsWildRoyal() => Assert.Equal(VideoPokerCategory.WildRoyal, Cat(
            C(FaceValue.Two, Suit.Clubs), C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades)));

        [Fact] public void WildRoyal_RanksBelow_NaturalRoyal()
        {
            var natural = W(C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades));
            var wild = W(C(FaceValue.Two, Suit.Clubs), C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades));
            Assert.True(natural.CompareTo(wild) > 0);
        }

        [Fact] public void ThreeAces_PlusTwoDeuces_IsFiveOfAKind()
        {
            var r = W(C(FaceValue.Two, Suit.Clubs), C(FaceValue.Two, Suit.Diamonds), C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Ace, Suit.Diamonds));
            Assert.Equal(VideoPokerCategory.FiveOfAKind, r.Category);
            Assert.Equal(14, r.PrimaryRank);
            Assert.Equal(2, r.WildCount);
        }

        [Fact] public void AllFourDeuces_IsFlaggedByWildCount()
        {
            var r = W(C(FaceValue.Two, Suit.Spades), C(FaceValue.Two, Suit.Hearts), C(FaceValue.Two, Suit.Diamonds), C(FaceValue.Two, Suit.Clubs), C(FaceValue.King, Suit.Spades));
            Assert.Equal(4, r.WildCount);   // the paytable pays this as the Four Deuces bonus
        }

        [Fact] public void DeuceCompletesQuads() // 3 sevens + a deuce = four sevens
        {
            var r = W(C(FaceValue.Two, Suit.Clubs), C(FaceValue.Seven, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Seven, Suit.Diamonds), C(FaceValue.King, Suit.Spades));
            Assert.Equal(VideoPokerCategory.FourOfAKind, r.Category);
            Assert.Equal(7, r.PrimaryRank);
        }

        [Fact] public void DeuceCompletesFlush() => Assert.Equal(VideoPokerCategory.Flush, Cat(
            C(FaceValue.Two, Suit.Diamonds), C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Clubs)));

        [Fact] public void DeuceCompletesStraight() => Assert.Equal(VideoPokerCategory.Straight, Cat(
            C(FaceValue.Two, Suit.Spades), C(FaceValue.Six, Suit.Hearts), C(FaceValue.Seven, Suit.Diamonds), C(FaceValue.Eight, Suit.Clubs), C(FaceValue.Nine, Suit.Spades)));

        [Fact] public void DeuceCompletesTrips() // 2 kings + a deuce = three kings (the Deuces minimum paying hand)
        {
            var r = W(C(FaceValue.Two, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Five, Suit.Clubs), C(FaceValue.Eight, Suit.Spades));
            Assert.Equal(VideoPokerCategory.ThreeOfAKind, r.Category);
            Assert.Equal(13, r.PrimaryRank);
        }

        [Fact] public void ZeroDeuces_MatchesNaturalEvaluate()
        {
            var hand = new List<Card> { C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Nine, Suit.Spades) };
            Assert.Equal(VideoPokerEvaluator.Evaluate(hand).Category, VideoPokerEvaluator.EvaluateWild(hand, Deuce).Category);
        }

        // ---- Deuces Wild full-pay paytable (25-15-9-5-3-2) ----

        [Fact]
        public void DeucesPaytable_OneCoin()
        {
            Assert.Equal(250, DW.Payout(W(C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades)), 1)); // natural royal
            Assert.Equal(200, DW.Payout(W(C(FaceValue.Two, Suit.Spades), C(FaceValue.Two, Suit.Hearts), C(FaceValue.Two, Suit.Diamonds), C(FaceValue.Two, Suit.Clubs), C(FaceValue.King, Suit.Spades)), 1)); // four deuces
            Assert.Equal(25,  DW.Payout(W(C(FaceValue.Two, Suit.Clubs), C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades)), 1)); // wild royal
            Assert.Equal(15,  DW.Payout(W(C(FaceValue.Two, Suit.Clubs), C(FaceValue.Two, Suit.Diamonds), C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Ace, Suit.Diamonds)), 1)); // five of a kind
            Assert.Equal(9,   DW.Payout(W(C(FaceValue.Two, Suit.Spades), C(FaceValue.Six, Suit.Spades), C(FaceValue.Seven, Suit.Spades), C(FaceValue.Eight, Suit.Spades), C(FaceValue.Nine, Suit.Spades)), 1)); // straight flush
            Assert.Equal(5,   DW.Payout(W(C(FaceValue.Two, Suit.Clubs), C(FaceValue.Seven, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Seven, Suit.Diamonds), C(FaceValue.King, Suit.Spades)), 1)); // four of a kind
            Assert.Equal(3,   DW.Payout(W(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Queen, Suit.Clubs), C(FaceValue.Queen, Suit.Spades)), 1)); // full house
            Assert.Equal(2,   DW.Payout(W(C(FaceValue.Two, Suit.Diamonds), C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Clubs)), 1)); // flush
            Assert.Equal(2,   DW.Payout(W(C(FaceValue.Two, Suit.Spades), C(FaceValue.Six, Suit.Hearts), C(FaceValue.Seven, Suit.Diamonds), C(FaceValue.Eight, Suit.Clubs), C(FaceValue.Nine, Suit.Spades)), 1)); // straight
            Assert.Equal(1,   DW.Payout(W(C(FaceValue.Two, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Five, Suit.Clubs), C(FaceValue.Eight, Suit.Spades)), 1)); // three of a kind
        }

        [Fact]
        public void DeucesPaytable_NonPayingHands_ReturnZero()
        {
            // a deuce that only makes a pair (of kings) — below the Deuces minimum (three of a kind)
            Assert.Equal(0, DW.Payout(W(C(FaceValue.Two, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.Nine, Suit.Diamonds), C(FaceValue.Five, Suit.Clubs), C(FaceValue.Three, Suit.Spades)), 1));
            // a natural (no-deuce) two pair pays nothing in Deuces Wild
            Assert.Equal(0, DW.Payout(W(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Nine, Suit.Spades)), 1));
        }

        [Fact]
        public void DeucesPaytable_RoyalSpecial_VsWildRoyalLinear()
        {
            var naturalRoyal = W(C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades));
            var wildRoyal = W(C(FaceValue.Two, Suit.Clubs), C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades));
            var fourDeuces = W(C(FaceValue.Two, Suit.Spades), C(FaceValue.Two, Suit.Hearts), C(FaceValue.Two, Suit.Diamonds), C(FaceValue.Two, Suit.Clubs), C(FaceValue.King, Suit.Spades));

            Assert.Equal(4000, DW.Payout(naturalRoyal, 5));   // natural royal jackpots at max bet
            Assert.Equal(1000, DW.Payout(naturalRoyal, 4));   // still linear below max
            Assert.Equal(125,  DW.Payout(wildRoyal, 5));      // wild royal stays linear (25 × 5)
            Assert.Equal(1000, DW.Payout(fourDeuces, 5));     // four deuces is linear (200 × 5), no max-bet jump
        }
    }
}
