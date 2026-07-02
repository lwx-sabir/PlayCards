using CardGames.Blackjack;
using Khela.Common.Stats;
using Khela.Game.Services.Stats;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>Locks the per-seat → lifetime stat-counter roll-up (blackjacks/doubles/busts/splits/insurance).
    /// Pure: HandSettlement in → counter-delta bag out.</summary>
    public class BlackjackStatCountersTests
    {
        private static HandSettlement H(HandOutcome o, int handIndex = 0, bool doubled = false, bool pair = false,
            decimal insStake = 0m, InsuranceResult ins = InsuranceResult.None)
            => new HandSettlement { HandIndex = handIndex, Outcome = o, Doubled = doubled, WasDealtPair = pair, InsuranceStake = insStake, Insurance = ins };

        [Fact]
        public void Blackjack_CountsBlackjackAndWin()
        {
            var c = BlackjackStatCounters.ForSeat(new[] { H(HandOutcome.Blackjack) });
            Assert.Equal(1L, c[GameStatKeys.HandsPlayed]);
            Assert.Equal(1L, c[GameStatKeys.Blackjacks]);
            Assert.Equal(1L, c[GameStatKeys.HandsWon]);   // a natural is also a win
            Assert.False(c.ContainsKey(GameStatKeys.HandsLost));
            Assert.False(c.ContainsKey(GameStatKeys.Splits));
        }

        [Fact]
        public void Bust_CountsBustAndLoss()
        {
            var c = BlackjackStatCounters.ForSeat(new[] { H(HandOutcome.Bust) });
            Assert.Equal(1L, c[GameStatKeys.Busts]);
            Assert.Equal(1L, c[GameStatKeys.HandsLost]);  // a bust is also a loss
            Assert.False(c.ContainsKey(GameStatKeys.HandsWon));
        }

        [Fact]
        public void SplitTwoHands_CountsOneSplitOnePairPlusOutcomes()
        {
            // A split implies the opening hand was a pair — the flag rides hand 0, counted once.
            var c = BlackjackStatCounters.ForSeat(new[] { H(HandOutcome.Win, 0, pair: true), H(HandOutcome.Lose, 1) });
            Assert.Equal(2L, c[GameStatKeys.HandsPlayed]);
            Assert.Equal(1L, c[GameStatKeys.Splits]);     // 2 hands → 1 split action
            Assert.Equal(1L, c[GameStatKeys.Pairs]);      // opening pair, counted once per seat
            Assert.Equal(1L, c[GameStatKeys.HandsWon]);
            Assert.Equal(1L, c[GameStatKeys.HandsLost]);
        }

        [Fact]
        public void PairDealtButNotSplit_CountsPairNotSplit()
        {
            var c = BlackjackStatCounters.ForSeat(new[] { H(HandOutcome.Win, 0, pair: true) });
            Assert.Equal(1L, c[GameStatKeys.Pairs]);
            Assert.False(c.ContainsKey(GameStatKeys.Splits));   // dealt a pair but chose not to split
        }

        [Fact]
        public void ThreeHands_CountTwoSplits()
        {
            var c = BlackjackStatCounters.ForSeat(new[] { H(HandOutcome.Win, 0), H(HandOutcome.Win, 1), H(HandOutcome.Lose, 2) });
            Assert.Equal(2L, c[GameStatKeys.Splits]);     // 3 hands → 2 splits
        }

        [Fact]
        public void Doubled_And_InsuranceWon_AreCounted()
        {
            var c = BlackjackStatCounters.ForSeat(new[] { H(HandOutcome.Win, doubled: true, insStake: 50m, ins: InsuranceResult.Win) });
            Assert.Equal(1L, c[GameStatKeys.Doubles]);
            Assert.Equal(1L, c[GameStatKeys.InsuranceTaken]);
            Assert.Equal(1L, c[GameStatKeys.InsuranceWon]);
        }

        [Fact]
        public void InsuranceTakenButLost_NotCountedAsWon()
        {
            var c = BlackjackStatCounters.ForSeat(new[] { H(HandOutcome.Lose, insStake: 50m, ins: InsuranceResult.Lose) });
            Assert.Equal(1L, c[GameStatKeys.InsuranceTaken]);
            Assert.False(c.ContainsKey(GameStatKeys.InsuranceWon));
        }

        [Fact]
        public void Push_CountsPushOnly()
        {
            var c = BlackjackStatCounters.ForSeat(new[] { H(HandOutcome.Push) });
            Assert.Equal(1L, c[GameStatKeys.Pushes]);
            Assert.False(c.ContainsKey(GameStatKeys.HandsWon));
            Assert.False(c.ContainsKey(GameStatKeys.HandsLost));
        }

        [Fact]
        public void Empty_ReturnsEmptyBag()
        {
            Assert.Empty(BlackjackStatCounters.ForSeat(new HandSettlement[0]));
        }
    }
}
