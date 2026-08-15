using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CardGames.Platforms;
using CardGames.VideoPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks Joker Poker: the 53rd wild card (a single joker) in the deck, and the wild evaluator scoring it exactly
    /// like a deuce (it just uses <c>IsJoker</c> as the wild predicate). The joker carries a distinct deck-hash token
    /// and is only ever read as "wild", never by rank/suit.
    /// </summary>
    public class VideoPokerJokerTests
    {
        private static Card C(FaceValue f, Suit s) => new Card(s, f, true);
        private static Card J() => Card.Joker();
        private static readonly Func<Card, bool> IsJoker = c => c.IsJoker;
        private static VideoPokerHandRank W(params Card[] cs) => VideoPokerEvaluator.EvaluateWild(cs.ToList(), IsJoker);
        private static VideoPokerCategory Cat(params Card[] cs) => W(cs).Category;

        // ---- the 53-card deck ----

        [Fact]
        public void Deal_WithOneJoker_Is53Cards_ExactlyOneJoker()
        {
            var g = new VideoPokerGame();
            g.Deal(Encoding.UTF8.GetBytes("joker-seed-1"), jokers: 1);
            Assert.Equal(5, g.Dealt.Count);
            Assert.Equal(53, g.Dealt.Count + g.Deck.Cards.Count);                  // 5 dealt + 48 remaining
            Assert.Equal(1, g.Dealt.Concat(g.Deck.Cards).Count(c => c.IsJoker));   // exactly one joker in the deck
        }

        [Fact]
        public void StandardDeal_HasNoJoker()
        {
            var g = new VideoPokerGame();
            g.Deal(Encoding.UTF8.GetBytes("no-joker"));   // jokers defaults to 0
            Assert.Equal(52, g.Dealt.Count + g.Deck.Cards.Count);
            Assert.DoesNotContain(g.Dealt.Concat(g.Deck.Cards), c => c.IsJoker);
        }

        [Fact]
        public void Joker_DeckHash_DiffersFromAceOfSpades()   // the joker's placeholder face must not collide in the hash
        {
            var withJoker = new[] { J(), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades) };
            var withAce = new[] { C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades) };
            Assert.NotEqual(CardGames.Provable.ProvableShuffle.DeckHash(withJoker), CardGames.Provable.ProvableShuffle.DeckHash(withAce));
        }

        // ---- joker evaluation ----

        [Fact] public void NaturalRoyal_NoJoker_IsRoyalFlush() => Assert.Equal(VideoPokerCategory.RoyalFlush, Cat(
            C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades), C(FaceValue.Ten, Suit.Spades)));

        [Fact] public void JokerCompletesRoyal_IsWildRoyal() => Assert.Equal(VideoPokerCategory.WildRoyal, Cat(
            J(), C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Spades), C(FaceValue.Jack, Suit.Spades)));

        [Fact] public void JokerPlusFourAces_IsFiveOfAKind()
        {
            var r = W(J(), C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Ace, Suit.Diamonds), C(FaceValue.Ace, Suit.Clubs));
            Assert.Equal(VideoPokerCategory.FiveOfAKind, r.Category);
            Assert.Equal(14, r.PrimaryRank);
            Assert.Equal(1, r.WildCount);
        }

        [Fact] public void JokerCompletesQuads() => Assert.Equal(VideoPokerCategory.FourOfAKind, Cat(
            J(), C(FaceValue.Seven, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Seven, Suit.Diamonds), C(FaceValue.King, Suit.Spades)));

        [Fact] public void JokerCompletesFlush() => Assert.Equal(VideoPokerCategory.Flush, Cat(
            J(), C(FaceValue.Ace, Suit.Clubs), C(FaceValue.Nine, Suit.Clubs), C(FaceValue.Seven, Suit.Clubs), C(FaceValue.Four, Suit.Clubs)));

        [Fact] public void JokerCompletesStraight() => Assert.Equal(VideoPokerCategory.Straight, Cat(
            J(), C(FaceValue.Six, Suit.Hearts), C(FaceValue.Seven, Suit.Diamonds), C(FaceValue.Eight, Suit.Clubs), C(FaceValue.Nine, Suit.Spades)));

        [Fact] public void JokerMakesPairOfKings_ForKingsOrBetterGate()
        {
            var r = W(J(), C(FaceValue.King, Suit.Hearts), C(FaceValue.Nine, Suit.Diamonds), C(FaceValue.Five, Suit.Clubs), C(FaceValue.Three, Suit.Spades));
            Assert.Equal(VideoPokerCategory.Pair, r.Category);
            Assert.Equal(13, r.PrimaryRank);   // pair of Kings — the Joker-Poker minimum paying hand
        }
    }
}
