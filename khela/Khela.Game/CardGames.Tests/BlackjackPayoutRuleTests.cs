using CardGames.Blackjack;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks the rule-derived payout table and the settle invariant tripwire (Part A): payouts come from
    /// explicit rules, and a rule-vs-engine mismatch is flagged while still paying the rule value.
    /// </summary>
    public class BlackjackPayoutRuleTests
    {
        [Theory]
        [InlineData(HandOutcome.Blackjack, 2.5)]
        [InlineData(HandOutcome.Win, 2)]
        [InlineData(HandOutcome.Push, 1)]
        [InlineData(HandOutcome.Lose, 0)]
        [InlineData(HandOutcome.Bust, 0)]
        public void MainMultiplier_MatchesRuleTable(HandOutcome outcome, double mult)
            => Assert.Equal((decimal)mult, BlackjackSettlement.MainPayoutMultiplier(outcome));

        [Fact]
        public void GrossReturn_Natural_Pays2_5x()
            => Assert.Equal(2500m, BlackjackSettlement.GrossReturnFor(HandOutcome.Blackjack, 1000m, InsuranceResult.None, 0m));

        [Fact]
        public void GrossReturn_Win_Pays2x()
            => Assert.Equal(2000m, BlackjackSettlement.GrossReturnFor(HandOutcome.Win, 1000m, InsuranceResult.None, 0m));

        [Fact]
        public void GrossReturn_Push_ReturnsStake()
            => Assert.Equal(1000m, BlackjackSettlement.GrossReturnFor(HandOutcome.Push, 1000m, InsuranceResult.None, 0m));

        [Theory]
        [InlineData(HandOutcome.Lose)]
        [InlineData(HandOutcome.Bust)]
        public void GrossReturn_Loss_PaysZero(HandOutcome outcome)
            => Assert.Equal(0m, BlackjackSettlement.GrossReturnFor(outcome, 1000m, InsuranceResult.None, 0m));

        [Fact]
        public void GrossReturn_InsuranceWin_AddsTripleInsuranceStake()
            // hand lost (0) but insurance won: 500 * 3 = 1500 returned.
            => Assert.Equal(1500m, BlackjackSettlement.GrossReturnFor(HandOutcome.Lose, 1000m, InsuranceResult.Win, 500m));

        [Fact]
        public void GrossReturn_InsuranceLose_AddsNothing()
            => Assert.Equal(2000m, BlackjackSettlement.GrossReturnFor(HandOutcome.Win, 1000m, InsuranceResult.Lose, 500m));

        [Fact]
        public void Reconcile_WhenMatched_PaysRuleValue_NoMismatch()
        {
            var (credit, mismatch) = BlackjackSettlement.ReconcilePayout(2500m, 2500m);
            Assert.Equal(2500m, credit);
            Assert.False(mismatch);
        }

        [Fact]
        public void Reconcile_WhenEngineDrifts_StillPaysRuleValue_AndFlags()
        {
            // Engine mirror wrongly added 3000 (e.g. a future AddWin multiplier bug); rules say 2500.
            var (credit, mismatch) = BlackjackSettlement.ReconcilePayout(2500m, 3000m);
            Assert.Equal(2500m, credit);   // the rule value is always the payer
            Assert.True(mismatch);         // and the tripwire fires
        }
    }
}
