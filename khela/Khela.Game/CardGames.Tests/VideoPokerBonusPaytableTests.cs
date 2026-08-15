using System.Linq;
using CardGames.Platforms;
using CardGames.VideoPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks the Bonus-family paytables — the rank-tiered four-of-a-kind of Bonus Poker / Double Bonus, and the
    /// kicker premiums of Double Double Bonus (four aces + a 2/3/4, four 2s-4s + an A/2/3/4) — plus Joker Poker's
    /// Five of a Kind / Wild Royal rows and its Kings-or-Better gate. Payouts read the evaluator's PrimaryRank + Kicker.
    /// </summary>
    public class VideoPokerBonusPaytableTests
    {
        private static Card C(FaceValue f, Suit s) => new Card(s, f, true);
        private static VideoPokerHandRank Eval(params Card[] cs) => VideoPokerEvaluator.Evaluate(cs.ToList());
        private static VideoPokerHandRank Joker(params Card[] cs) => VideoPokerEvaluator.EvaluateWild(cs.ToList(), c => c.IsJoker);

        // quads of `rank` with a chosen `kicker` (natural — Bonus family is non-wild)
        private static VideoPokerHandRank Quads(FaceValue rank, FaceValue kicker) => Eval(
            C(rank, Suit.Spades), C(rank, Suit.Hearts), C(rank, Suit.Diamonds), C(rank, Suit.Clubs), C(kicker, Suit.Spades));

        [Fact]
        public void BonusPoker_QuadTiers_NoKickerDistinction()
        {
            var pt = VideoPokerPaytable.BonusPoker85;
            Assert.Equal(80, pt.Payout(Quads(FaceValue.Ace, FaceValue.King), 1));    // four aces
            Assert.Equal(80, pt.Payout(Quads(FaceValue.Ace, FaceValue.Two), 1));     // four aces — kicker irrelevant here
            Assert.Equal(40, pt.Payout(Quads(FaceValue.Three, FaceValue.King), 1));  // four 2s-4s
            Assert.Equal(25, pt.Payout(Quads(FaceValue.Queen, FaceValue.Two), 1));   // four 5s-Ks
            Assert.Equal(8,  pt.Payout(Eval(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.Queen, Suit.Diamonds), C(FaceValue.Queen, Suit.Spades)), 1)); // full house 8
        }

        [Fact]
        public void DoubleBonus_QuadTiers_TwoPairDropsToOne()
        {
            var pt = VideoPokerPaytable.DoubleBonus975;
            Assert.Equal(160, pt.Payout(Quads(FaceValue.Ace, FaceValue.Nine), 1));
            Assert.Equal(80,  pt.Payout(Quads(FaceValue.Four, FaceValue.King), 1));
            Assert.Equal(50,  pt.Payout(Quads(FaceValue.Ten, FaceValue.Two), 1));
            Assert.Equal(7,   pt.Payout(Eval(C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Clubs), C(FaceValue.Two, Suit.Clubs)), 1)); // flush 7
            Assert.Equal(1,   pt.Payout(Eval(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.King, Suit.Clubs), C(FaceValue.King, Suit.Diamonds), C(FaceValue.Two, Suit.Spades)), 1)); // two pair 1
        }

        [Fact]
        public void DoubleDoubleBonus_KickerPremiums()
        {
            var pt = VideoPokerPaytable.DoubleDoubleBonus96;
            Assert.Equal(400, pt.Payout(Quads(FaceValue.Ace, FaceValue.Two), 1));    // four aces + a 2/3/4 kicker
            Assert.Equal(400, pt.Payout(Quads(FaceValue.Ace, FaceValue.Four), 1));
            Assert.Equal(160, pt.Payout(Quads(FaceValue.Ace, FaceValue.King), 1));   // four aces, ordinary kicker
            Assert.Equal(160, pt.Payout(Quads(FaceValue.Three, FaceValue.Ace), 1));  // four 2s-4s + an A/2/3/4 kicker
            Assert.Equal(160, pt.Payout(Quads(FaceValue.Two, FaceValue.Four), 1));
            Assert.Equal(80,  pt.Payout(Quads(FaceValue.Three, FaceValue.Nine), 1)); // four 2s-4s, ordinary kicker
            Assert.Equal(50,  pt.Payout(Quads(FaceValue.King, FaceValue.Two), 1));   // four 5s-Ks (no kicker premium)
        }

        [Fact]
        public void JokerPoker_Rows_AndKingsGate()
        {
            var pt = VideoPokerPaytable.JokerPokerKings;
            // five of a kind (joker + four aces) = 200
            Assert.Equal(200, pt.Payout(Joker(Card.Joker(), C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Ace, Suit.Diamonds), C(FaceValue.Ace, Suit.Clubs)), 1));
            // wild royal (joker completes A-K-Q-J-10) = 100
            Assert.Equal(100, pt.Payout(Joker(Card.Joker(), C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades)), 1));
            // flat four of a kind = 20
            Assert.Equal(20, pt.Payout(Quads(FaceValue.Seven, FaceValue.King), 1));
            // Kings or Better gate: pair of Kings pays, pair of Queens does not
            Assert.Equal(1, pt.Payout(Eval(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Five, Suit.Diamonds), C(FaceValue.Two, Suit.Spades)), 1));
            Assert.Equal(0, pt.Payout(Eval(C(FaceValue.Queen, Suit.Spades), C(FaceValue.Queen, Suit.Hearts), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Five, Suit.Diamonds), C(FaceValue.Two, Suit.Spades)), 1));
        }
    }
}
