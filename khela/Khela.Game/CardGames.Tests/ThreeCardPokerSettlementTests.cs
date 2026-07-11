using System.Collections.Generic;
using System.Linq;
using CardGames.Platforms;
using CardGames.ThreeCardPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks Three Card Poker settlement against the spec: the four dealer-qualify branches, fold forfeits
    /// (but side bets + no Ante Bonus), Ante Bonus EVEN ON A LOSS / only-if-played, side-bets-pay-on-fold, the
    /// mini-royal top line, and the exact enumerated house edges over all 22,100 hands.
    /// </summary>
    public class ThreeCardPokerSettlementTests
    {
        private static Card C(FaceValue f, Suit s) => new Card(s, f, true);
        private static List<Card> H(params Card[] cs) => cs.ToList();
        private static readonly ThreeCardPokerPaytables PT = ThreeCardPokerPaytables.Default;

        private static List<Card> QueenHigh() => H(C(FaceValue.Queen, Suit.Spades), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Four, Suit.Clubs)); // qualifies
        private static List<Card> JackHigh()  => H(C(FaceValue.Jack,  Suit.Spades), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Four, Suit.Clubs)); // does NOT qualify
        private static List<Card> AceHigh()   => H(C(FaceValue.Ace,   Suit.Spades), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Four, Suit.Clubs)); // beats Q-high
        private static ThreeCardPokerBets Ante(decimal a) => new() { Ante = a };

        // ---- dealer qualification (Queen-high) ----
        [Fact] public void Dealer_QueenHigh_Qualifies() =>
            Assert.True(ThreeCardPokerSettlement.DealerQualifies(ThreeCardEvaluator.Evaluate(QueenHigh())));
        [Fact] public void Dealer_JackHigh_DoesNotQualify() =>
            Assert.False(ThreeCardPokerSettlement.DealerQualifies(ThreeCardEvaluator.Evaluate(JackHigh())));
        [Fact] public void Dealer_AnyPair_Qualifies() =>
            Assert.True(ThreeCardPokerSettlement.DealerQualifies(ThreeCardEvaluator.Evaluate(
                H(C(FaceValue.Two, Suit.Spades), C(FaceValue.Two, Suit.Hearts), C(FaceValue.Five, Suit.Clubs)))));

        // ---- the four settlement branches ----
        [Fact]
        public void NoQualify_AntePays1to1_PlayPushes()
        {
            var r = ThreeCardPokerSettlement.Settle(AceHigh(), JackHigh(), Ante(100m), played: true, PT);
            Assert.Equal("no_qualify", r.Outcome);
            Assert.Equal(200m, r.AnteReturn);   // 1:1
            Assert.Equal(100m, r.PlayReturn);   // push
        }

        [Fact]
        public void Qualify_PlayerWins_BothPay1to1()
        {
            var r = ThreeCardPokerSettlement.Settle(AceHigh(), QueenHigh(), Ante(100m), played: true, PT);
            Assert.Equal("win", r.Outcome);
            Assert.Equal(200m, r.AnteReturn);
            Assert.Equal(200m, r.PlayReturn);
        }

        [Fact]
        public void Qualify_PlayerLoses_BothLose()
        {
            var r = ThreeCardPokerSettlement.Settle(QueenHigh(), AceHigh(), Ante(100m), played: true, PT);
            Assert.Equal("lose", r.Outcome);
            Assert.Equal(0m, r.AnteReturn);
            Assert.Equal(0m, r.PlayReturn);
        }

        [Fact]
        public void Qualify_Tie_BothPush()
        {
            var a = H(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Four, Suit.Clubs));
            var b = H(C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Nine, Suit.Clubs),  C(FaceValue.Four, Suit.Diamonds));
            var r = ThreeCardPokerSettlement.Settle(a, b, Ante(100m), played: true, PT);
            Assert.Equal("push", r.Outcome);
            Assert.Equal(100m, r.AnteReturn);
            Assert.Equal(100m, r.PlayReturn);
        }

        [Fact]
        public void Fold_AnteForfeited_NoPlay_NoAnteBonus()
        {
            var straight = H(C(FaceValue.Nine, Suit.Spades), C(FaceValue.Ten, Suit.Hearts), C(FaceValue.Jack, Suit.Clubs));
            var r = ThreeCardPokerSettlement.Settle(straight, QueenHigh(), Ante(100m), played: false, PT);
            Assert.Equal("fold", r.Outcome);
            Assert.Equal(0m, r.AnteReturn);
            Assert.Equal(0m, r.PlayReturn);
            Assert.Equal(0m, r.AnteBonus);      // fold forfeits the Ante Bonus even with a straight
        }

        // ---- Ante Bonus: pays even on a LOSS; only if Played ----
        [Fact]
        public void AnteBonus_PaysStraight_EvenWhenHandLoses()
        {
            var straight    = H(C(FaceValue.Nine, Suit.Spades), C(FaceValue.Ten, Suit.Hearts), C(FaceValue.Jack, Suit.Clubs)); // J-high straight
            var dealerTrips = H(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Clubs)); // beats it
            var r = ThreeCardPokerSettlement.Settle(straight, dealerTrips, Ante(100m), played: true, PT);
            Assert.Equal("lose", r.Outcome);
            Assert.Equal(0m, r.AnteReturn);     // lost the showdown
            Assert.Equal(100m, r.AnteBonus);    // straight bonus 1:1 still pays
        }

        // ---- side bets pay on a FOLD ----
        [Fact]
        public void PairPlus_PaysOnFold()
        {
            var pair = H(C(FaceValue.Five, Suit.Spades), C(FaceValue.Five, Suit.Hearts), C(FaceValue.King, Suit.Clubs));
            var bets = new ThreeCardPokerBets { Ante = 100m, PairPlus = 50m };
            var r = ThreeCardPokerSettlement.Settle(pair, QueenHigh(), bets, played: false, PT);
            Assert.Equal("fold", r.Outcome);
            Assert.Equal(0m, r.AnteReturn);
            Assert.Equal(100m, r.PairPlusReturn);   // pair 1:1 → gross 2× stake, even folded
        }

        [Fact]
        public void PairPlus_MiniRoyal_TopLine_WhenEnabled()
        {
            var pt = new ThreeCardPokerPaytables { PairPlusMiniRoyal = 100 };
            var miniRoyal = H(C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades));
            var bets = new ThreeCardPokerBets { Ante = 10m, PairPlus = 10m };
            var withTop    = ThreeCardPokerSettlement.Settle(miniRoyal, QueenHigh(), bets, true, pt);
            var withoutTop = ThreeCardPokerSettlement.Settle(miniRoyal, QueenHigh(), bets, true, PT);   // default: no top line
            Assert.Equal(10m * (100 + 1), withTop.PairPlusReturn);    // 100:1 top line
            Assert.Equal(10m * (40 + 1),  withoutTop.PairPlusReturn); // falls back to SF rate 40:1
        }

        // ---- Prime ----
        [Fact]
        public void Prime_ThreePlayerSameColour_Pays()
        {
            var redThree = H(C(FaceValue.Two, Suit.Hearts), C(FaceValue.Nine, Suit.Diamonds), C(FaceValue.King, Suit.Hearts)); // all red
            var mixedDealer = H(C(FaceValue.Three, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Ten, Suit.Clubs));
            var bets = new ThreeCardPokerBets { Ante = 10m, Prime = 5m };
            var r = ThreeCardPokerSettlement.Settle(redThree, mixedDealer, bets, played: true, PT);
            Assert.Equal(5m * (3 + 1), r.PrimeReturn);   // 3:1
        }

        [Fact]
        public void Prime_AllSixSameColour_PaysHigher()
        {
            var redP = H(C(FaceValue.Two, Suit.Hearts), C(FaceValue.Nine, Suit.Diamonds), C(FaceValue.King, Suit.Hearts));
            var redD = H(C(FaceValue.Three, Suit.Hearts), C(FaceValue.Seven, Suit.Diamonds), C(FaceValue.Ten, Suit.Hearts));
            var bets = new ThreeCardPokerBets { Ante = 10m, Prime = 5m };
            var r = ThreeCardPokerSettlement.Settle(redP, redD, bets, played: true, PT);
            Assert.Equal(5m * (4 + 1), r.PrimeReturn);   // all-6 pays 4:1
        }

        // ---- enumerated EV over all 22,100 3-card hands (validates evaluator combos + paytable math) ----
        [Fact]
        public void PairPlus_Default_HouseEdge_Is_About_2point32pct()
        {
            decimal net = 0m; int n = 0;
            foreach (var h in AllThreeCardHands())
            {
                var r = ThreeCardPokerSettlement.Settle(h, h, new ThreeCardPokerBets { PairPlus = 1m }, played: false, PT);
                net += r.PairPlusReturn - 1m;   // gross − stake
                n++;
            }
            Assert.Equal(22100, n);
            double edge = (double)(-net / n);
            Assert.InRange(edge, 0.0231, 0.0233);   // spec: 40/30/6/4/1 → 2.32% (exact = 512/22100)
        }

        [Fact]
        public void AnteBonus_Default_AddsAbout_5point3pct()
        {
            decimal bonus = 0m; int n = 0;
            foreach (var h in AllThreeCardHands())
            {
                var r = ThreeCardPokerSettlement.Settle(h, QueenHigh(), new ThreeCardPokerBets { Ante = 1m }, played: true, PT);
                bonus += r.AnteBonus;   // pure winnings, no stake
                n++;
            }
            double ev = (double)(bonus / n);
            Assert.InRange(ev, 0.0527, 0.0530);   // (720×1 + 52×4 + 48×5)/22100 = 0.05285
        }

        private static IEnumerable<List<Card>> AllThreeCardHands()
        {
            var deck = new Deck().Cards;   // 52 distinct cards
            for (int i = 0; i < deck.Count; i++)
                for (int j = i + 1; j < deck.Count; j++)
                    for (int k = j + 1; k < deck.Count; k++)
                        yield return new List<Card> { deck[i], deck[j], deck[k] };
        }
    }
}
