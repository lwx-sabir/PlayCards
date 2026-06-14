using System;
using System.Linq;
using CardGames.Platforms;
using Xunit;

namespace CardGames.Tests
{
    public class ShoeTests
    {
        [Fact]
        public void SingleDeck_Has52Cards() => Assert.Equal(52, new Deck().Cards.Count);

        [Fact]
        public void SixDeckShoe_Has312Cards() => Assert.Equal(312, new Deck(6).Cards.Count);

        [Fact]
        public void SixDeckShoe_EachCardAppearsSixTimes()
        {
            var shoe = new Deck(6);
            var groups = shoe.Cards.GroupBy(c => (c.Suit, c.FaceVal)).ToList();

            Assert.Equal(52, groups.Count);                       // all 52 distinct cards present
            Assert.All(groups, g => Assert.Equal(6, g.Count()));  // each exactly six times
        }

        [Fact]
        public void Shoe_DealsPastASingleDeckWithoutRunningDry()
        {
            var shoe = new Deck(6);
            for (int i = 0; i < 100; i++)   // 100 > 52: the single-deck bug would have thrown here
                Assert.NotNull(shoe.Draw());

            Assert.Equal(212, shoe.Cards.Count);
        }

        [Fact]
        public void Shoe_RejectsZeroOrNegativeDecks()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Deck(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Deck(-1));
        }
    }
}
