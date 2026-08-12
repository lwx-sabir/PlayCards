using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using CardGames.Blackjack;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// The multi-deck SHOE rules. A shoe of more than one deck must PERSIST across rounds (that is what makes a cut
    /// card meaningful at all), while a single deck keeps the original behaviour of reshuffling every round. Getting
    /// this wrong is invisible in play but changes the game's maths, so it is regression-locked.
    /// </summary>
    public class BlackjackShoeTests
    {
        private static byte[] Seed(string s) => Encoding.UTF8.GetBytes(s);

        private static BlackJackGame GameWithOnePlayer()
        {
            var g = new BlackJackGame();
            g.Players.Add(new Player("p1", 1000m, "P1") { InRound = true, SeatNumber = 1 });
            return g;
        }

        [Fact]
        public void NewShoe_HasFiftyTwoCardsPerDeck()
        {
            var g = GameWithOnePlayer();
            g.StartShoe(Seed("s"), 6);
            Assert.Equal(312, g.CardsRemaining);
        }

        [Fact]
        public void DealingFromTheSameShoe_ConsumesItProgressively()
        {
            // The whole point of a persistent shoe: round 2 continues where round 1 stopped.
            var g = GameWithOnePlayer();
            g.StartShoe(Seed("shoe"), 6);

            g.DealNewGame(null, 6, newShoe: false);
            int afterFirst = g.CardsRemaining;

            g.DealNewGame(null, 6, newShoe: false);
            int afterSecond = g.CardsRemaining;

            Assert.Equal(312 - 4, afterFirst);          // 2 cards each to one player + the dealer
            Assert.Equal(afterFirst - 4, afterSecond);  // NOT reset back to 312
        }

        [Fact]
        public void NewShoeTrue_ReshufflesEveryRound()
        {
            // Single-deck / legacy behaviour: each round starts from a full shoe.
            var g = GameWithOnePlayer();
            g.DealNewGame(Seed("a"), 1, newShoe: true);
            int afterFirst = g.CardsRemaining;
            g.DealNewGame(Seed("b"), 1, newShoe: true);
            Assert.Equal(afterFirst, g.CardsRemaining);
        }

        [Fact]
        public void DealNewGame_DefaultsToReshuffling_SoExistingCallersAreUnchanged()
        {
            var g = GameWithOnePlayer();
            g.DealNewGame(Seed("x"), 1);
            int afterFirst = g.CardsRemaining;
            g.DealNewGame(Seed("x"));
            Assert.Equal(afterFirst, g.CardsRemaining);
        }

        [Fact]
        public void AnExhaustedShoe_IsReplacedRatherThanDealingOffTheEnd()
        {
            // Safety net: Deck.Draw() has no empty guard, so a shoe that can't cover the deal must be rebuilt
            // instead of throwing mid-round.
            var g = GameWithOnePlayer();
            g.StartShoe(Seed("tiny"), 1);
            while (g.CardsRemaining > 3) g.Deck.Draw();   // leave fewer than the 4 this deal needs

            g.DealNewGame(Seed("tiny"), 6, newShoe: false);

            Assert.True(g.CardsRemaining > 0);
            Assert.Equal(2, g.Players[0].Hands[0].Hand.Cards.Count);
            Assert.Equal(2, g.Dealer.Hand.Cards.Count);
        }

        [Fact]
        public void AShoeThatRunsDryMidRoundExtendsItselfInsteadOfFailing()
        {
            // A round has no hard card limit — resplits and long low-card hands can out-run any cut card. Failing
            // part-way through would leave live stakes on a hand that can never finish, so the shoe grows instead.
            var g = GameWithOnePlayer();
            g.StartShoe(Seed("dry"), 1);
            while (g.CardsRemaining > 0) g.Deck.Draw();

            var card = g.Deck.Draw();          // would have thrown

            Assert.NotNull(card);
            Assert.Equal(1, g.ShoeExtensions);
            Assert.Equal(51, g.CardsRemaining);
        }

        [Fact]
        public void TheExtensionIsReproducibleFromTheShoeSeed()
        {
            // Replay depends on this: the extra cards must be derivable by anyone holding the recorded seed.
            static List<string> DrainPastTheEnd(string seed)
            {
                var g = new BlackJackGame();
                g.StartShoe(Encoding.UTF8.GetBytes(seed), 1);
                while (g.CardsRemaining > 0) g.Deck.Draw();
                var drawn = new List<string>();
                for (int i = 0; i < 10; i++) drawn.Add(g.Deck.Draw().ToString());
                return drawn;
            }

            Assert.Equal(DrainPastTheEnd("same"), DrainPastTheEnd("same"));
            Assert.NotEqual(DrainPastTheEnd("same"), DrainPastTheEnd("different"));
        }

        [Fact]
        public void ExtensionSurvivesTheJsonRoundTripAndStaysWiredUp()
        {
            // OnExhausted is not serialised; AttachDeck re-wires it. A shoe reloaded mid-round must still extend.
            var g = new BlackJackGame();
            g.StartShoe(Seed("reload"), 1);
            while (g.CardsRemaining > 0) g.Deck.Draw();

            var reloaded = JsonSerializer.Deserialize<BlackJackGame>(JsonSerializer.Serialize(g));
            reloaded.AttachDeck();

            Assert.NotNull(reloaded.Deck.Draw());
            Assert.Equal(1, reloaded.ShoeExtensions);
        }

        [Fact]
        public void SameSeed_ProducesTheSameShoeOrder()
        {
            // Replay depends on this: rebuilding from the recorded seed must reproduce the exact shoe.
            var a = new BlackJackGame(); a.StartShoe(Seed("replay"), 6);
            var b = new BlackJackGame(); b.StartShoe(Seed("replay"), 6);
            Assert.Equal(a.Deck.ComputeHash(), b.Deck.ComputeHash());
        }
    }
}
