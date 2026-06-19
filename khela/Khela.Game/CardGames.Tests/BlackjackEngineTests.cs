using System.Collections.Generic;
using System.Text;
using CardGames.Platforms;
using CardGames.Blackjack;
// NOTE: Player / BlackJackGame live in the DOUBLED namespace `CardGames.Blackjack.CardGames.Blackjack`
// (a known wart — CLAUDE.md flags removing the doubled `namespace CardGames.Blackjack`). Referenced
// as-is here so the tests track the real type locations; collapse this import when that cleanup lands.
using CardGames.Blackjack.CardGames.Blackjack;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>Ace-aware hand evaluation: face cards = 10, Ace = 11 or 1, soft vs hard, bust.</summary>
    public class BlackjackHandTests
    {
        private static Card C(FaceValue v, Suit s = Suit.Spades, bool up = true) => new Card(s, v, up);

        private static BlackJackHand Hand(params Card[] cards)
        {
            var h = new BlackJackHand();
            foreach (var c in cards) h.Cards.Add(c);
            return h;
        }

        [Fact]
        public void NumberCards_SumPips()
            => Assert.Equal(12, Hand(C(FaceValue.Seven), C(FaceValue.Five)).GetSumOfHand());

        [Fact]
        public void FaceCards_CountAsTen()
            => Assert.Equal(20, Hand(C(FaceValue.King), C(FaceValue.Queen)).GetSumOfHand());

        [Fact]
        public void AceKing_Is21_AndSoft()
        {
            var v = Hand(C(FaceValue.Ace), C(FaceValue.King)).GetSumOfHand(out var soft);
            Assert.Equal(21, v);
            Assert.True(soft);
        }

        [Fact]
        public void TwoAces_Is12()
        {
            var v = Hand(C(FaceValue.Ace), C(FaceValue.Ace)).GetSumOfHand(out var soft);
            Assert.Equal(12, v);   // one Ace 11, one Ace 1
            Assert.True(soft);
        }

        [Fact]
        public void SoftSeventeen_BecomesHard_WhenTenAdded()
        {
            var hand = Hand(C(FaceValue.Ace), C(FaceValue.Six));
            Assert.Equal(17, hand.GetSumOfHand(out var soft1));
            Assert.True(soft1);                                 // A+6 = soft 17

            hand.Cards.Add(C(FaceValue.Ten));
            Assert.Equal(17, hand.GetSumOfHand(out var soft2)); // A drops to 1: 1+6+10
            Assert.False(soft2);                                // now hard 17
        }

        [Fact]
        public void Bust_Over21()
            => Assert.Equal(25, Hand(C(FaceValue.King), C(FaceValue.Queen), C(FaceValue.Five)).GetSumOfHand());

        [Fact]
        public void MultipleAces_DropAsNeeded()
        {
            // A+A+A+8: 11+1+1+8 = 21, one Ace still counts as 11 -> soft
            var v = Hand(C(FaceValue.Ace), C(FaceValue.Ace), C(FaceValue.Ace), C(FaceValue.Eight)).GetSumOfHand(out var soft);
            Assert.Equal(21, v);
            Assert.True(soft);
        }

        [Fact]
        public void VisibleSum_IgnoresFaceDownCards()
        {
            var hand = Hand(C(FaceValue.King, Suit.Spades, true), C(FaceValue.Ace, Suit.Hearts, false));
            Assert.Equal(10, hand.GetVisibleSum()); // only the face-up King counts
            Assert.Equal(21, hand.GetSumOfHand());  // full hand is still 21
        }
    }

    /// <summary>Player actions: blackjack/bust detection, settle math, split, double-down.</summary>
    public class BlackjackPlayerTests
    {
        private static Card C(FaceValue v, Suit s = Suit.Spades) => new Card(s, v, true);

        private static Player PlayerWith(decimal balance, params Card[] cards)
        {
            var p = new Player("p1", balance, "P1");
            foreach (var c in cards) p.GetHand(0).Hand.Cards.Add(c);
            return p;
        }

        [Fact]
        public void HasBlackjack_TwoCard21()
            => Assert.True(PlayerWith(1000m, C(FaceValue.Ace), C(FaceValue.King)).HasBlackJack());

        [Fact]
        public void ThreeCard21_IsNotBlackjack()
        {
            var p = PlayerWith(1000m, C(FaceValue.Seven), C(FaceValue.Seven), C(FaceValue.Seven));
            Assert.Equal(21, p.GetHand(0).Hand.GetSumOfHand());
            Assert.False(p.HasBlackJack());
            Assert.False(p.HasBust());
        }

        [Fact]
        public void HasBust_Over21()
            => Assert.True(PlayerWith(1000m, C(FaceValue.King), C(FaceValue.Queen), C(FaceValue.Five)).HasBust());

        [Fact]
        public void AddWin_EvenMoney_Pays2xBet()
        {
            var p = PlayerWith(1000m, C(FaceValue.Ten), C(FaceValue.Nine));
            p.GetHand(0).Bet = 100m;
            p.AddWin(2m);                          // stake-back + equal winnings
            Assert.Equal(1200m, p.Balance);
            Assert.Equal(1, p.Wins);
            Assert.Equal(0m, p.GetHand(0).Bet);
        }

        [Fact]
        public void AddWin_Blackjack_Pays2Point5xBet()
        {
            // 3:2 natural: a 100 stake returns 250 (stake + 1.5x). Locks the payout math the settle
            // path must use for a natural blackjack.
            var p = PlayerWith(1000m, C(FaceValue.Ace), C(FaceValue.King));
            p.GetHand(0).Bet = 100m;
            p.AddWin(2.5m);
            Assert.Equal(1250m, p.Balance);
        }

        [Fact]
        public void AddPush_ReturnsBet()
        {
            var p = PlayerWith(1000m, C(FaceValue.Ten), C(FaceValue.Eight));
            p.GetHand(0).Bet = 100m;
            p.AddPush();
            Assert.Equal(1100m, p.Balance);
            Assert.Equal(1, p.Push);
            Assert.Equal(0m, p.GetHand(0).Bet);
        }

        [Fact]
        public void Split_NonPair_Throws()
        {
            var p = PlayerWith(1000m, C(FaceValue.Eight), C(FaceValue.Nine));
            p.GetHand(0).Bet = 100m;
            p.CurrentDeck = new Deck();
            Assert.Throws<System.InvalidOperationException>(() => p.Split());
        }

        [Fact]
        public void Split_Pair_CreatesSecondHand_AndChargesSecondStake()
        {
            var p = PlayerWith(1000m, C(FaceValue.Eight, Suit.Spades), C(FaceValue.Eight, Suit.Hearts));
            p.GetHand(0).Bet = 100m;
            p.CurrentDeck = new Deck();

            var idx = p.Split();
            Assert.Equal(1, idx);
            Assert.Equal(2, p.Hands.Count);
            Assert.Equal(2, p.GetHand(0).Hand.Cards.Count); // each split hand drew one card
            Assert.Equal(2, p.GetHand(1).Hand.Cards.Count);
            Assert.Equal(900m, p.Balance);                  // paid the second stake
        }

        [Fact]
        public void Split_TenValuePair_DifferentRanks_Splits()
        {
            // Casino-standard: any two 10-value cards (e.g. King + Queen) form a splittable pair.
            var p = PlayerWith(1000m, C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Hearts));
            p.GetHand(0).Bet = 100m;
            p.CurrentDeck = new Deck();

            var idx = p.Split();
            Assert.Equal(1, idx);
            Assert.Equal(2, p.Hands.Count);
        }

        [Fact]
        public void Split_Aces_DrawOneCardEach_AndLockBothHands()
        {
            var p = PlayerWith(1000m, C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts));
            p.GetHand(0).Bet = 100m;
            p.CurrentDeck = new Deck();

            p.Split();
            Assert.Equal(2, p.GetHand(0).Hand.Cards.Count); // exactly one card added to each
            Assert.Equal(2, p.GetHand(1).Hand.Cards.Count);
            Assert.True(p.GetHand(0).Done);                 // split aces can't be hit/doubled/re-split
            Assert.True(p.GetHand(1).Done);
        }

        [Fact]
        public void DoubleDown_DoublesBet_DrawsExactlyOneCard_AndLocksHand()
        {
            var p = PlayerWith(1000m, C(FaceValue.Five), C(FaceValue.Six));
            p.GetHand(0).Bet = 100m;
            p.CurrentDeck = new Deck();

            var r = p.DoubleDown();
            Assert.Equal(200m, r.NewBet);
            Assert.Equal(900m, p.Balance);                  // one extra stake deducted
            Assert.Equal(3, p.GetHand(0).Hand.Cards.Count); // drew exactly one card
            Assert.True(p.GetHand(0).Done);
        }
    }

    /// <summary>Game flow: the deal, the dealer's hole card, and dealer-play termination.</summary>
    public class BlackjackGameTests
    {
        private static byte[] Seed(string s) => Encoding.UTF8.GetBytes(s);

        private static BlackJackGame NewGameInRound(int players)
        {
            var infos = new List<Player>();
            for (int i = 0; i < players; i++) infos.Add(new Player($"p{i}", 1000m, $"P{i}"));
            var game = new BlackJackGame(infos);
            foreach (var p in game.Players) p.InRound = true; // only in-round players are dealt
            return game;
        }

        [Fact]
        public void DealNewGame_DealsTwoCardsEach_DealerHoleFaceDown()
        {
            var game = NewGameInRound(2);
            game.DealNewGame(Seed("deal-test"), 1);

            foreach (var p in game.Players)
                Assert.Equal(2, p.Hand.Cards.Count);
            Assert.Equal(2, game.Dealer.Hand.Cards.Count);
            Assert.True(game.Dealer.Hand.Cards[0].IsCardUp);
            Assert.False(game.Dealer.Hand.Cards[1].IsCardUp); // hole card hidden
        }

        [Fact]
        public void DealerPlay_StandsAtLeast17_AndRevealsHole()
        {
            var game = NewGameInRound(1);
            game.DealNewGame(Seed("dealer-test"), 1);
            game.DealerPlay();

            Assert.True(game.Dealer.Hand.Cards[1].IsCardUp);        // hole revealed
            Assert.True(game.Dealer.Hand.GetSumOfHand() >= 17);     // dealer always finishes on 17+ (or bust)
        }

        [Fact]
        public void DealNewGame_SkipsPlayersNotInRound()
        {
            var game = NewGameInRound(2);
            game.Players[1].InRound = false;
            game.DealNewGame(Seed("skip-test"), 1);

            Assert.Equal(2, game.Players[0].Hand.Cards.Count);
            Assert.Empty(game.Players[1].Hand.Cards);
        }
    }
}
