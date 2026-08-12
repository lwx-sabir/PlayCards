using System.Collections.Generic;
using System.Linq;
using System.Text;
using CardGames.Platforms;
using CardGames.VideoPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks the pure video-poker engine: a clean 5-card deal, hold-then-draw from the SAME committed deck, one-draw
    /// enforcement, and — the provably-fair guarantee — that the same seed + same hold reproduces the identical final
    /// hand (the draw is pre-committed, not re-randomised after the hold).
    /// </summary>
    public class VideoPokerGameTests
    {
        private static readonly byte[] Seed = Encoding.UTF8.GetBytes("khela-video-poker-test-seed-01");
        private static string Key(Card c) => $"{(int)c.FaceVal}-{(int)c.Suit}";
        private static string Str(IEnumerable<Card> cs) => string.Join(",", cs.Select(Key));

        [Fact]
        public void Deal_FiveDistinctCards_AndAStableHash()
        {
            var g = new VideoPokerGame();
            g.Deal(Seed);
            Assert.Equal(5, g.Dealt.Count);
            Assert.Equal(5, g.Dealt.Select(Key).Distinct().Count());   // no duplicates
            Assert.False(string.IsNullOrEmpty(g.DeckHash()));

            var g2 = new VideoPokerGame(); g2.Deal(Seed);
            Assert.Equal(g.DeckHash(), g2.DeckHash());                 // same seed → same committed deck
            Assert.Equal(Str(g.Dealt), Str(g2.Dealt));
        }

        [Fact]
        public void HoldAll_KeepsTheDealtHand()
        {
            var g = new VideoPokerGame(); g.Deal(Seed);
            var before = Str(g.Dealt);
            var final = g.Draw(new[] { true, true, true, true, true });
            Assert.Equal(before, Str(final));
        }

        [Fact]
        public void HoldNone_ReplacesAllFive_FromTheRemainder()
        {
            var g = new VideoPokerGame(); g.Deal(Seed);
            var dealt = g.Dealt.Select(Key).ToHashSet();
            var final = g.Draw(new[] { false, false, false, false, false });
            Assert.Equal(5, final.Count);
            Assert.Equal(5, final.Select(Key).Distinct().Count());     // 5 distinct
            Assert.True(final.All(c => !dealt.Contains(Key(c))));       // all fresh (from deck positions 5..9)
        }

        [Fact]
        public void PartialHold_KeepsHeldReplacesRest()
        {
            var g = new VideoPokerGame(); g.Deal(Seed);
            var dealt = g.Dealt.ToList();
            var final = g.Draw(new[] { true, false, true, false, true });
            Assert.Equal(Key(dealt[0]), Key(final[0]));   // held
            Assert.Equal(Key(dealt[2]), Key(final[2]));
            Assert.Equal(Key(dealt[4]), Key(final[4]));
            Assert.Equal(5, final.Select(Key).Distinct().Count());
        }

        [Fact]
        public void SameSeedSameHold_ReproducesTheFinalHand()   // provably-fair: draw is committed, not re-rolled
        {
            var hold = new[] { true, false, true, false, true };
            var a = new VideoPokerGame(); a.Deal(Seed); var fa = a.Draw(hold);
            var b = new VideoPokerGame(); b.Deal(Seed); var fb = b.Draw(hold);
            Assert.Equal(Str(fa), Str(fb));
        }

        [Fact]
        public void SecondDraw_Throws()
        {
            var g = new VideoPokerGame(); g.Deal(Seed);
            g.Draw(new[] { true, true, true, true, true });
            Assert.Throws<System.InvalidOperationException>(() => g.Draw(new[] { false, false, false, false, false }));
        }

        [Fact]
        public void EvaluateBeforeDraw_Throws()
        {
            var g = new VideoPokerGame(); g.Deal(Seed);
            Assert.Throws<System.InvalidOperationException>(() => g.Evaluate());
        }
    }
}
