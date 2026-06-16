using CardGames.Platforms;
using CardGames.Blackjack.CardGames.Blackjack;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// The settle DECISION logic (which the manager reconciles to the wallet): win/loss/push, the 3:2
    /// natural, both-naturals push, dealer-natural-beats-non-natural, dealer bust, and insurance.
    /// Asserts the mirror-balance delta each outcome produces (= the gross the wallet is credited).
    /// Player starts at 1000 with the stake NOT pre-deducted, so the delta is purely the payout.
    /// </summary>
    public class BlackjackSettlementTests
    {
        private static Card C(FaceValue v, Suit s = Suit.Spades) => new Card(s, v, true);

        private static (BlackJackGame game, Player player) Setup(Card[] dealer, Card[] player, decimal bet = 100m)
        {
            var game = new BlackJackGame();
            foreach (var c in dealer) game.Dealer.Hand.Cards.Add(c);

            var p = new Player("p1", 1000m, "P1") { InRound = true };
            foreach (var c in player) p.GetHand(0).Hand.Cards.Add(c);
            p.GetHand(0).Bet = bet;
            game.Players.Add(p);
            return (game, p);
        }

        [Fact]
        public void PlayerBeatsDealer_PaysEvenMoney()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ten), C(FaceValue.Seven) },   // 17
                                  player: new[] { C(FaceValue.Ten), C(FaceValue.Ten) });     // 20
            BlackjackSettlement.Settle(game);
            Assert.Equal(1200m, p.Balance); // +2x stake
            Assert.Equal(1, p.Wins);
        }

        [Fact]
        public void PlayerLowerThanDealer_Loses()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ten), C(FaceValue.Ten) },      // 20
                                  player: new[] { C(FaceValue.Ten), C(FaceValue.Eight) });    // 18
            BlackjackSettlement.Settle(game);
            Assert.Equal(1000m, p.Balance); // nothing returned
            Assert.Equal(1, p.Losses);
        }

        [Fact]
        public void EqualTotals_Push_ReturnsStake()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ten), C(FaceValue.Nine) },      // 19
                                  player: new[] { C(FaceValue.Ten), C(FaceValue.Nine) });     // 19
            BlackjackSettlement.Settle(game);
            Assert.Equal(1100m, p.Balance); // stake returned
            Assert.Equal(1, p.Push);
        }

        [Fact]
        public void PlayerBust_Loses()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ten), C(FaceValue.Eight) },     // 18
                                  player: new[] { C(FaceValue.Ten), C(FaceValue.Ten), C(FaceValue.Five) }); // 25
            BlackjackSettlement.Settle(game);
            Assert.Equal(1000m, p.Balance);
            Assert.Equal(1, p.Losses);
        }

        [Fact]
        public void DealerBust_PlayerWins()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ten), C(FaceValue.Ten), C(FaceValue.Five) }, // 25
                                  player: new[] { C(FaceValue.Ten), C(FaceValue.Eight) });    // 18
            BlackjackSettlement.Settle(game);
            Assert.Equal(1200m, p.Balance);
            Assert.Equal(1, p.Wins);
        }

        [Fact]
        public void PlayerNatural_Pays3To2()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ten), C(FaceValue.Eight) },     // 18
                                  player: new[] { C(FaceValue.Ace), C(FaceValue.King) });     // natural 21
            BlackjackSettlement.Settle(game);
            Assert.Equal(1250m, p.Balance); // 2.5x stake (3:2)
            Assert.Equal(1, p.Wins);
        }

        [Fact]
        public void BothNaturals_Push()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ace), C(FaceValue.King) },      // natural
                                  player: new[] { C(FaceValue.Ace), C(FaceValue.Queen) });    // natural
            BlackjackSettlement.Settle(game);
            Assert.Equal(1100m, p.Balance); // stake returned, no 3:2
            Assert.Equal(1, p.Push);
        }

        [Fact]
        public void DealerNatural_BeatsNonNatural()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ace), C(FaceValue.King) },      // natural
                                  player: new[] { C(FaceValue.Ten), C(FaceValue.Ten) });      // 20, not natural
            BlackjackSettlement.Settle(game);
            Assert.Equal(1000m, p.Balance);
            Assert.Equal(1, p.Losses);
        }

        [Fact]
        public void Insurance_Pays2To1_WhenDealerHasNatural()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ace), C(FaceValue.King) },      // natural
                                  player: new[] { C(FaceValue.Ten), C(FaceValue.Eight) });    // 18, loses main
            p.GetHand(0).InsuranceBet = 50m;
            BlackjackSettlement.Settle(game);
            Assert.Equal(1150m, p.Balance);     // insurance returns 3x (50 -> 150); main bet lost
            Assert.Equal(0m, p.GetHand(0).InsuranceBet);
            Assert.Equal(1, p.Losses);
        }

        [Fact]
        public void Insurance_Forfeited_WhenDealerHasNoNatural()
        {
            var (game, p) = Setup(dealer: new[] { C(FaceValue.Ten), C(FaceValue.Seven) },     // 17, no natural
                                  player: new[] { C(FaceValue.Ten), C(FaceValue.Eight) });    // 18, wins main
            p.GetHand(0).InsuranceBet = 50m;
            BlackjackSettlement.Settle(game);
            Assert.Equal(1200m, p.Balance);     // no insurance payout; main bet wins even money
            Assert.Equal(0m, p.GetHand(0).InsuranceBet);
            Assert.Equal(1, p.Wins);
        }

        [Fact]
        public void SplitHands_SettleIndependently()
        {
            var game = new BlackJackGame();
            game.Dealer.Hand.Cards.Add(C(FaceValue.Ten));
            game.Dealer.Hand.Cards.Add(C(FaceValue.Eight)); // dealer 18

            var p = new Player("p1", 1000m, "P1") { InRound = true };
            // hand 0: 20 (wins)
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Ten));
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Ten));
            p.GetHand(0).Bet = 100m;
            // hand 1: 16 (loses)
            var h1 = new PlayerHandState { Bet = 100m };
            h1.Hand.Cards.Add(C(FaceValue.Ten, Suit.Hearts));
            h1.Hand.Cards.Add(C(FaceValue.Six));
            p.Hands.Add(h1);
            game.Players.Add(p);

            BlackjackSettlement.Settle(game);

            Assert.Equal(1200m, p.Balance); // hand0 +200, hand1 +0
            Assert.Equal(1, p.Wins);
            Assert.Equal(1, p.Losses);
        }
    }
}
