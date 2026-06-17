using System.Collections.Generic;
using System.Linq;
using System.Text;
using CardGames.Platforms;
using CardGames.Provable;
using Xunit;

namespace CardGames.Tests
{
    public class ProvableShuffleTests
    {
        private static byte[] Server(string s) => Encoding.UTF8.GetBytes(s);
        private static List<string> Order(Deck d) => d.Cards.Select(ProvableShuffle.Canonical).ToList();

        [Fact]
        public void Shuffle_SameSeed_SameOrder()
        {
            var seed = ProvableShuffle.DeriveSeed(Server("server"), "client", 1);
            var a = new Deck(); var b = new Deck();
            ProvableShuffle.Shuffle(a.Cards, seed);
            ProvableShuffle.Shuffle(b.Cards, seed);
            Assert.Equal(Order(a), Order(b)); // the headline: fully reproducible
        }

        [Fact]
        public void Shuffle_DifferentNonce_DifferentOrder()
        {
            var s1 = ProvableShuffle.DeriveSeed(Server("server"), "client", 1);
            var s2 = ProvableShuffle.DeriveSeed(Server("server"), "client", 2);
            var a = new Deck(); var b = new Deck();
            ProvableShuffle.Shuffle(a.Cards, s1);
            ProvableShuffle.Shuffle(b.Cards, s2);
            Assert.NotEqual(Order(a), Order(b));
        }

        [Fact]
        public void Shuffle_PreservesTheMultiset()
        {
            var seed = ProvableShuffle.DeriveSeed(Server("server"), "client", 7);
            var deck = new Deck();
            var before = Order(deck).OrderBy(x => x).ToList();
            ProvableShuffle.Shuffle(deck.Cards, seed);
            var after = Order(deck).OrderBy(x => x).ToList();

            Assert.Equal(before, after); // same 52 cards — none lost, none duplicated
            Assert.Equal(52, deck.Cards.Count);
        }

        [Fact]
        public void Shuffle_ActuallyReorders()
        {
            var seed = ProvableShuffle.DeriveSeed(Server("server"), "client", 1);
            var deck = new Deck();
            var before = Order(deck);
            ProvableShuffle.Shuffle(deck.Cards, seed);
            Assert.NotEqual(before, Order(deck));
        }

        [Fact]
        public void DeriveSeed_Deterministic_AndSensitiveToEveryInput()
        {
            var baseline = ProvableShuffle.DeriveSeed(Server("server"), "client", 1);
            Assert.Equal(baseline, ProvableShuffle.DeriveSeed(Server("server"), "client", 1));
            Assert.NotEqual(baseline, ProvableShuffle.DeriveSeed(Server("server"), "client", 2));
            Assert.NotEqual(baseline, ProvableShuffle.DeriveSeed(Server("server"), "other", 1));
            Assert.NotEqual(baseline, ProvableShuffle.DeriveSeed(Server("SERVER"), "client", 1));
        }

        [Fact]
        public void DeckHash_StableForSameOrder_ChangesWhenReordered()
        {
            var seed = ProvableShuffle.DeriveSeed(Server("server"), "client", 1);
            var a = new Deck(); ProvableShuffle.Shuffle(a.Cards, seed);
            var b = new Deck(); ProvableShuffle.Shuffle(b.Cards, seed);
            Assert.Equal(ProvableShuffle.DeckHash(a.Cards), ProvableShuffle.DeckHash(b.Cards));

            (b.Cards[0], b.Cards[1]) = (b.Cards[1], b.Cards[0]);
            Assert.NotEqual(ProvableShuffle.DeckHash(a.Cards), ProvableShuffle.DeckHash(b.Cards));
        }

        [Fact]
        public void Canonical_FormatsRankAndSuit()
        {
            Assert.Equal("14H", ProvableShuffle.Canonical(new Card(Suit.Hearts, FaceValue.Ace, true)));
            Assert.Equal("2D", ProvableShuffle.Canonical(new Card(Suit.Diamonds, FaceValue.Two, true)));
            Assert.Equal("13S", ProvableShuffle.Canonical(new Card(Suit.Spades, FaceValue.King, true)));
            Assert.Equal("10C", ProvableShuffle.Canonical(new Card(Suit.Clubs, FaceValue.Ten, true)));
        }

        [Fact]
        public void KnownAnswer_LocksTheAlgorithm()
        {
            // Pins exact output for a fixed seed, so a future refactor cannot silently change
            // shuffle results and invalidate verification of historical hands.
            var seed = ProvableShuffle.DeriveSeed(Server("server-seed-KAT"), "client-KAT", 1);
            var deck = new Deck();
            ProvableShuffle.Shuffle(deck.Cards, seed);

            var top8 = string.Join(",", deck.Cards.Take(8).Select(ProvableShuffle.Canonical));
            Assert.Equal("7H,14D,10S,4D,10C,3S,12D,4C", top8);
        }
    }
}
