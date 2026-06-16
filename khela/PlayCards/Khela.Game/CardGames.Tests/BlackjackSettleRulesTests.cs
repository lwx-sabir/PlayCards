using CardGames.Platforms;
using CardGames.Blackjack;
using CardGames.Blackjack.CardGames.Blackjack;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// The rules that move the house edge: insurance (2:1), the dealer's soft-17 vs hard-17 decision,
    /// and double-down paying on the doubled stake. These exercise the Player/Game logic directly.
    /// (The settle DECISION logic — SettleRound choosing 3:2 for a natural, both-naturals push, split
    /// hands, and the net-to-wallet reconciliation — needs SettleRound extracted to a pure function or
    /// a MySQL/Redis integration harness; tracked separately.)
    /// </summary>
    public class BlackjackSettleRulesTests
    {
        private static Card C(FaceValue v, Suit s = Suit.Spades, bool up = true) => new Card(s, v, up);

        // ---- Insurance: 2:1, capped at half the bet, must be positive ----

        [Fact]
        public void Insurance_Pays2To1_NetGainTwiceTheStake()
        {
            var p = new Player("p1", 1000m, "P1");
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Ten));
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Six));
            p.GetHand(0).Bet = 100m;

            p.PlaceInsurance(50m);          // max = half the bet; 1000 -> 950
            Assert.Equal(950m, p.Balance);

            p.AddInsurancePayout(50m);      // 2:1 -> stake back + 2x = +150 -> 1100
            Assert.Equal(1100m, p.Balance); // net +100 over the original 1000 (2:1 on the 50 stake)
        }

        [Fact]
        public void Insurance_CannotExceedHalfTheBet()
        {
            var p = new Player("p1", 1000m, "P1");
            p.GetHand(0).Bet = 100m;
            Assert.Throws<System.InvalidOperationException>(() => p.PlaceInsurance(60m));
        }

        [Fact]
        public void Insurance_MustBePositive()
        {
            var p = new Player("p1", 1000m, "P1");
            p.GetHand(0).Bet = 100m;
            Assert.Throws<System.InvalidOperationException>(() => p.PlaceInsurance(0m));
        }

        // ---- Dealer soft-17 / hard-17 rule ----

        private static BlackJackGame DealerWith(bool standOnSoft17, params Card[] dealerCards)
        {
            var game = new BlackJackGame { StandOnSoft17 = standOnSoft17, StandOnHard17 = true };
            foreach (var c in dealerCards) game.Dealer.Hand.Cards.Add(c);
            game.Dealer.CurrentDeck = new Deck();   // so the dealer can draw if it hits
            return game;
        }

        [Fact]
        public void DealerPlay_StandsOnSoft17_WhenConfigured()
        {
            var game = DealerWith(standOnSoft17: true, C(FaceValue.Ace), C(FaceValue.Six)); // soft 17
            game.DealerPlay();
            Assert.Equal(2, game.Dealer.Hand.Cards.Count); // stood — drew nothing
        }

        [Fact]
        public void DealerPlay_HitsSoft17_WhenConfigured()
        {
            var game = DealerWith(standOnSoft17: false, C(FaceValue.Ace), C(FaceValue.Six)); // soft 17
            game.DealerPlay();
            Assert.True(game.Dealer.Hand.Cards.Count >= 3); // hit at least once
        }

        [Fact]
        public void DealerPlay_StandsOnHard17()
        {
            var game = DealerWith(standOnSoft17: true, C(FaceValue.Ten), C(FaceValue.Seven)); // hard 17
            game.DealerPlay();
            Assert.Equal(2, game.Dealer.Hand.Cards.Count); // always stands on hard 17
        }

        // ---- Double-down pays on the doubled stake, not the original ----

        [Fact]
        public void DoubleDown_Win_PaysOnDoubledStake()
        {
            var p = new Player("p1", 1000m, "P1");
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Five));
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Six));
            p.GetHand(0).Bet = 100m;
            p.CurrentDeck = new Deck();

            p.DoubleDown();                       // bet -> 200, balance 1000 -> 900
            Assert.Equal(900m, p.Balance);
            Assert.Equal(200m, p.GetHand(0).Bet);

            p.AddWin(2m);                          // pays 2x the DOUBLED 200 = 400 -> 1300
            Assert.Equal(1300m, p.Balance);
        }
    }
}
