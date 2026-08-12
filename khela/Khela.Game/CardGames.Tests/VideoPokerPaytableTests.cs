using System.Collections.Generic;
using System.Linq;
using CardGames.Platforms;
using CardGames.VideoPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks the Jacks or Better 9/6 paytable: every category's per-coin payout, the "Jacks or Better" pair gate
    /// (pair of J+ pays, ten-or-lower does not), and the non-linear royal-flush max-bet jump (250/coin at 1–4, a flat
    /// 4,000 at 5 coins). Losing hands return 0.
    /// </summary>
    public class VideoPokerPaytableTests
    {
        private static Card C(FaceValue f, Suit s) => new Card(s, f, true);
        private static VideoPokerHandRank Eval(params Card[] cs) => VideoPokerEvaluator.Evaluate(cs.ToList());
        private static readonly VideoPokerPaytable PT = VideoPokerPaytable.JacksOrBetter96;

        private static readonly VideoPokerHandRank Royal = Eval(C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades));
        private static readonly VideoPokerHandRank SF = Eval(C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Eight, Suit.Hearts), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Six, Suit.Hearts), C(FaceValue.Five, Suit.Hearts));
        private static readonly VideoPokerHandRank Quads = Eval(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Ace, Suit.Diamonds), C(FaceValue.King, Suit.Spades));
        private static readonly VideoPokerHandRank FullHouse = Eval(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Queen, Suit.Spades));
        private static readonly VideoPokerHandRank Flush = Eval(C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Clubs), C(FaceValue.Two, Suit.Clubs));
        private static readonly VideoPokerHandRank Straight = Eval(C(FaceValue.Nine, Suit.Spades), C(FaceValue.Eight, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Six, Suit.Diamonds), C(FaceValue.Five, Suit.Spades));
        private static readonly VideoPokerHandRank Trips = Eval(C(FaceValue.Seven, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Two, Suit.Spades));
        private static readonly VideoPokerHandRank TwoPair = Eval(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Two, Suit.Spades));
        private static readonly VideoPokerHandRank JacksPair = Eval(C(FaceValue.Jack, Suit.Spades), C(FaceValue.Jack, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Nine, Suit.Spades));
        private static readonly VideoPokerHandRank TensPair = Eval(C(FaceValue.Ten, Suit.Spades), C(FaceValue.Ten, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Nine, Suit.Spades));
        private static readonly VideoPokerHandRank HighCard = Eval(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Diamonds), C(FaceValue.Two, Suit.Spades));

        [Fact]
        public void OneCoin_PayoutsMatchTheTable()
        {
            Assert.Equal(250, PT.Payout(Royal, 1));
            Assert.Equal(50,  PT.Payout(SF, 1));
            Assert.Equal(25,  PT.Payout(Quads, 1));
            Assert.Equal(9,   PT.Payout(FullHouse, 1));
            Assert.Equal(6,   PT.Payout(Flush, 1));
            Assert.Equal(4,   PT.Payout(Straight, 1));
            Assert.Equal(3,   PT.Payout(Trips, 1));
            Assert.Equal(2,   PT.Payout(TwoPair, 1));
            Assert.Equal(1,   PT.Payout(JacksPair, 1));
        }

        [Fact]
        public void JacksOrBetter_Gate()
        {
            Assert.Equal(1, PT.Payout(JacksPair, 1));   // pair of Jacks pays
            Assert.Equal(0, PT.Payout(TensPair, 1));    // pair of Tens does not
            Assert.Equal(0, PT.Payout(HighCard, 1));    // no pair, no win
        }

        [Fact]
        public void EverythingButRoyal_IsLinearInCoins()
        {
            Assert.Equal(250, PT.Payout(SF, 5));        // 50 × 5
            Assert.Equal(125, PT.Payout(Quads, 5));     // 25 × 5
            Assert.Equal(45,  PT.Payout(FullHouse, 5)); // 9 × 5
            Assert.Equal(30,  PT.Payout(Flush, 5));     // 6 × 5
            Assert.Equal(20,  PT.Payout(Straight, 5));
            Assert.Equal(15,  PT.Payout(Trips, 5));
            Assert.Equal(10,  PT.Payout(TwoPair, 5));
            Assert.Equal(5,   PT.Payout(JacksPair, 5));
        }

        [Fact]
        public void Royal_LinearBelowMaxBet_JackpotAtMax()
        {
            Assert.Equal(250,  PT.Payout(Royal, 1));    // 250 × 1
            Assert.Equal(1000, PT.Payout(Royal, 4));    // 250 × 4 (still linear)
            Assert.Equal(4000, PT.Payout(Royal, 5));    // the max-bet jackpot (800/coin), NOT 1250
        }
    }
}
