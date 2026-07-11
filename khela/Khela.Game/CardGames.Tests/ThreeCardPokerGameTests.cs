using System.Linq;
using System.Text;
using CardGames.ThreeCardPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>The 3CP deal: correct card counts, no collisions across seats + dealer from one 52-card deck,
    /// and deterministic replay from a seed (provably-fair).</summary>
    public class ThreeCardPokerGameTests
    {
        [Fact]
        public void Deal_GivesEachSeat3_AndDealer3_NoCollision()
        {
            var g = new ThreeCardPokerGame();
            g.DealNewGame(3, Encoding.UTF8.GetBytes("seed-1"));

            Assert.Equal(3, g.Seats.Count);
            Assert.All(g.Seats, s => Assert.Equal(3, s.Cards.Count));
            Assert.Equal(3, g.DealerCards.Count);

            var all = g.Seats.SelectMany(s => s.Cards).Concat(g.DealerCards).ToList();
            Assert.Equal(12, all.Count);
            var distinct = all.Select(c => ((int)c.FaceVal, c.Suit)).Distinct().Count();
            Assert.Equal(12, distinct);   // no duplicate card across the whole deal
        }

        [Fact]
        public void Deal_SameSeed_SameCards()
        {
            var seed = Encoding.UTF8.GetBytes("seed-xyz");
            var a = new ThreeCardPokerGame(); a.DealNewGame(4, seed);
            var b = new ThreeCardPokerGame(); b.DealNewGame(4, seed);
            Assert.Equal(Canon(a), Canon(b));

            static string Canon(ThreeCardPokerGame g) => string.Join("|",
                g.Seats.SelectMany(s => s.Cards).Concat(g.DealerCards).Select(c => $"{(int)c.FaceVal}{c.Suit}"));
        }
    }
}
