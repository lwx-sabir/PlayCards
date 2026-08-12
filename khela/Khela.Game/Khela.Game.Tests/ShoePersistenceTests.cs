using System.Text.Json;
using CardGames.Blackjack;
using Khela.Game.Managers;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// A table is stored as JSON between every single action, so the SHOE only holds together if it survives that
    /// round-trip. Object identity does not survive JSON, and everyone at the table draws through their own
    /// <c>CurrentDeck</c> reference — so these tests pin the contract that keeps one shoe one shoe.
    /// </summary>
    public class ShoePersistenceTests
    {
        private static BlackjackTable TableMidRound()
        {
            var t = new BlackjackTable { TableId = "t1", Game = new BlackJackGame() };
            t.Game.Players.Add(new Player("p1", 1000m, "P1") { InRound = true, SeatNumber = 1 });
            t.Game.Players.Add(new Player("p2", 1000m, "P2") { InRound = true, SeatNumber = 2 });
            t.Game.StartShoe(new byte[] { 1, 2, 3 }, 6);
            t.Game.DealNewGame(null, 6, newShoe: false);
            return t;
        }

        /// <summary>Mirrors the server's load path: deserialize, then re-attach the shoe (see GetTableAsync).</summary>
        private static BlackjackTable Reload(BlackjackTable t)
        {
            var loaded = JsonSerializer.Deserialize<BlackjackTable>(JsonSerializer.Serialize(t));
            loaded.Game.AttachDeck();
            return loaded;
        }

        [Fact]
        public void TheShoeSurvivesTheRoundTrip()
        {
            var before = TableMidRound();
            var after = Reload(before);

            Assert.NotNull(after.Game.Deck);
            Assert.Equal(before.Game.CardsRemaining, after.Game.CardsRemaining);
            Assert.Equal(before.Game.Deck.ComputeHash(), after.Game.Deck.ComputeHash());
        }

        [Fact]
        public void EveryoneAtTheTableDrawsFromTheSameShoe()
        {
            var after = Reload(TableMidRound());

            Assert.Same(after.Game.Deck, after.Game.Dealer.CurrentDeck);
            foreach (var p in after.Game.Players)
                Assert.Same(after.Game.Deck, p.CurrentDeck);
        }

        [Fact]
        public void ACardDrawnAfterReloadIsGoneForEveryoneElse()
        {
            // The consequence that actually matters: without one shared shoe, two seats can be dealt the same
            // physical card, and the dealt order stops matching the published shuffle.
            var after = Reload(TableMidRound());
            int before = after.Game.CardsRemaining;

            var drawn = after.Game.Players[0].CurrentDeck.Draw();

            Assert.DoesNotContain(after.Game.Players[1].CurrentDeck.Cards, c => ReferenceEquals(c, drawn));
            Assert.Equal(before - 1, after.Game.Players[1].CurrentDeck.Cards.Count);
            Assert.Equal(before - 1, after.Game.Dealer.CurrentDeck.Cards.Count);
            Assert.Equal(before - 1, after.Game.CardsRemaining);
        }

        [Fact]
        public void TheStoredTableCarriesExactlyOneCopyOfTheShoe()
        {
            // Each seat used to serialise its own full copy: a 3-seat six-deck table wrote well over a thousand
            // redundant card objects on EVERY action. Those copies are also what let the seats drift apart.
            var json = JsonSerializer.Serialize(TableMidRound());

            // The property itself is gone (note: the table's unrelated "CurrentDeckHash" must not match).
            Assert.DoesNotContain("\"CurrentDeck\":", json, System.StringComparison.Ordinal);

            // And the shoe appears exactly once: one "Cards" array holding the whole undealt shoe.
            int occurrences = 0;
            for (int i = json.IndexOf("\"Cards\"", System.StringComparison.Ordinal); i >= 0;
                     i = json.IndexOf("\"Cards\"", i + 1, System.StringComparison.Ordinal))
                occurrences++;
            // 1 shoe + 1 dealer hand + 2 player hands = 4, not 4 + one shoe copy per seat.
            Assert.Equal(4, occurrences);
        }

        [Fact]
        public void ShoeBookkeepingSurvivesTheRoundTrip()
        {
            // The cut card is decided from these, so they have to persist with the table, not be recomputed.
            var before = TableMidRound();
            before.ShoeNonce = 7;
            before.ShoeHash = "abc123";
            before.ShoeSize = 312;
            before.CutCardAt = 78;
            before.ShoeDealtAtRoundStart = 6;

            var after = Reload(before);

            Assert.Equal(7, after.ShoeNonce);
            Assert.Equal("abc123", after.ShoeHash);
            Assert.Equal(312, after.ShoeSize);
            Assert.Equal(78, after.CutCardAt);
            Assert.Equal(6, after.ShoeDealtAtRoundStart);
        }
    }
}
