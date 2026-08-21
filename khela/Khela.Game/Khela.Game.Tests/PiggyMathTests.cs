using Khela.Game.Services.Piggy;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// The piggy bank's rules, tested where they live — pure functions, no database, no clock.
    ///
    /// The cases that matter are the ones where two limits meet: a nearly-full bank on a nearly-capped day, a rate
    /// that would overfill, a tier ladder someone mis-authored. Those are exactly the ones that are invisible in a
    /// manual play test and expensive in production.
    /// </summary>
    public class PiggyMathTests
    {
        private static PiggyConfig Cfg(
            PiggyMode mode = PiggyMode.Wager,
            decimal wagerRate = 50m,
            decimal lossRate = 0m,
            decimal dayPercent = 25m,
            decimal breakPercent = 100m)
            => new PiggyConfig
            {
                Enabled = true,
                Mode = mode,
                WagerRatePercent = wagerRate,
                LossRatePercent = lossRate,
                MaxAccrualPerDayPercent = dayPercent,
                MinBreakPercent = breakPercent,
            };

        // ---- accrual ----

        [Fact]
        public void WagerModeBanksAShareOfTheStake()
            => Assert.Equal(1_000m, PiggyMath.Accrual(cleanWager: 2_000m, netLoss: 0m, Cfg()));

        [Fact]
        public void WagerModeIgnoresTheOutcome()
        {
            // The whole point of wager-based accrual: a winning round and a losing round of the same size bank the
            // same, so fill time is a function of play rather than of luck.
            var won = PiggyMath.Accrual(2_000m, netLoss: 0m, Cfg());
            var lost = PiggyMath.Accrual(2_000m, netLoss: 2_000m, Cfg());
            Assert.Equal(won, lost);
        }

        [Fact]
        public void LossModeBanksNothingOnAWin()
            => Assert.Equal(0m, PiggyMath.Accrual(2_000m, netLoss: 0m, Cfg(PiggyMode.Loss, lossRate: 20m)));

        [Fact]
        public void LossModeBanksAShareOfWhatWasLost()
            => Assert.Equal(400m, PiggyMath.Accrual(2_000m, netLoss: 2_000m, Cfg(PiggyMode.Loss, lossRate: 20m)));

        [Fact]
        public void BothModeAddsTheTwo()
            => Assert.Equal(1_400m, PiggyMath.Accrual(2_000m, 2_000m, Cfg(PiggyMode.Both, wagerRate: 50m, lossRate: 20m)));

        [Fact]
        public void GiftedOnlyRoundsBankNothing()
        {
            // The games pass a CLEAN wager, so a fully gifted stake arrives here as zero. Asserting it explicitly so
            // the guarantee is visible in the tests rather than only in the call site.
            Assert.Equal(0m, PiggyMath.Accrual(cleanWager: 0m, netLoss: 0m, Cfg()));
        }

        [Fact]
        public void NegativeInputsCannotDrainTheBank()
        {
            Assert.Equal(0m, PiggyMath.Accrual(-5_000m, 0m, Cfg()));
            Assert.Equal(0m, PiggyMath.Accrual(0m, -5_000m, Cfg(PiggyMode.Loss, lossRate: 20m)));
        }

        // ---- fitting: capacity and the daily cap ----

        [Fact]
        public void AccrualIsTruncatedToTheRoomLeft()
        {
            // 10 short of full, asked for 5,000: exactly 10 fits. The bank must never exceed its own maximum.
            var fits = PiggyMath.Fit(wanted: 5_000m, amount: 249_990m, max: 250_000m, accruedToday: 0m, Cfg(dayPercent: 0m));
            Assert.Equal(10m, fits);
        }

        [Fact]
        public void AFullBankStopsAccruing()
            => Assert.Equal(0m, PiggyMath.Fit(5_000m, amount: 250_000m, max: 250_000m, accruedToday: 0m, Cfg()));

        [Fact]
        public void TheDailyCapStopsAWhaleFillingItInOneSitting()
        {
            // 25% of 250,000 = 62,500 a day. Already banked 62,000 today, so only 500 more lands however much is bet.
            var fits = PiggyMath.Fit(wanted: 50_000m, amount: 62_000m, max: 250_000m, accruedToday: 62_000m, Cfg());
            Assert.Equal(500m, fits);
        }

        [Fact]
        public void CapacityAndTheDailyCapTakeTheSmaller()
        {
            // Nearly full AND nearly capped: 100 of capacity left, 2,500 of day left. Capacity wins.
            var fits = PiggyMath.Fit(wanted: 10_000m, amount: 249_900m, max: 250_000m, accruedToday: 60_000m, Cfg());
            Assert.Equal(100m, fits);
        }

        [Fact]
        public void ADailyCapOfZeroMeansUncapped()
        {
            var fits = PiggyMath.Fit(50_000m, amount: 0m, max: 250_000m, accruedToday: 999_999m, Cfg(dayPercent: 0m));
            Assert.Equal(50_000m, fits);
        }

        [Fact]
        public void TheDailyCapIsAShareOfCapacity()
            => Assert.Equal(62_500m, PiggyMath.DailyCap(250_000m, Cfg()));

        // ---- tiers ----

        [Theory]
        [InlineData(1, 1, 250_000)]
        [InlineData(9, 1, 250_000)]
        [InlineData(10, 2, 500_000)]
        [InlineData(24, 2, 500_000)]
        [InlineData(25, 3, 1_000_000)]
        [InlineData(50, 4, 2_500_000)]
        [InlineData(999, 4, 2_500_000)]
        public void TheLadderGivesTheHighestRungReached(int level, int expectedTier, decimal expectedMax)
        {
            var (tier, max, _) = PiggyMath.TierFor(level, Cfg());
            Assert.Equal(expectedTier, tier);
            Assert.Equal(expectedMax, max);
        }

        [Fact]
        public void ALadderThatStartsAboveLevelOneStillGivesABank()
        {
            // Mis-authored config: no rung a level-1 player qualifies for. They must get the lowest bank rather than
            // one of zero capacity, which would read as the feature being broken.
            var cfg = new PiggyConfig
            {
                Enabled = true,
                Tiers = new[]
                {
                    new PiggyTier { MinLevel = 20, MaxAmount = 900_000m },
                    new PiggyTier { MinLevel = 5,  MaxAmount = 100_000m },
                },
            };

            var (tier, max, _) = PiggyMath.TierFor(1, cfg);
            Assert.Equal(2, tier);
            Assert.Equal(100_000m, max);
        }

        [Fact]
        public void AnUnsortedLadderStillPicksTheBestRung()
        {
            var cfg = new PiggyConfig
            {
                Enabled = true,
                Tiers = new[]
                {
                    new PiggyTier { MinLevel = 25, MaxAmount = 1_000_000m },
                    new PiggyTier { MinLevel = 1,  MaxAmount =   250_000m },
                    new PiggyTier { MinLevel = 10, MaxAmount =   500_000m },
                },
            };

            var (_, max, _) = PiggyMath.TierFor(12, cfg);
            Assert.Equal(500_000m, max);
        }

        // ---- breaking ----

        [Fact]
        public void AFullBankCanBeBought()
            => Assert.True(PiggyMath.CanBreak(250_000m, 250_000m, Cfg()));

        [Fact]
        public void AnAlmostFullBankCannot()
            => Assert.False(PiggyMath.CanBreak(249_999m, 250_000m, Cfg()));

        [Fact]
        public void TheThresholdIsConfigurable()
            => Assert.True(PiggyMath.CanBreak(200_000m, 250_000m, Cfg(breakPercent: 80m)));

        [Fact]
        public void AnEmptyBankIsNeverBuyable()
            => Assert.False(PiggyMath.CanBreak(0m, 250_000m, Cfg(breakPercent: 0m)));

        // ---- the countdown ----

        private static readonly System.DateTime Now = new System.DateTime(2026, 8, 19, 12, 0, 0, System.DateTimeKind.Utc);

        private static PiggyConfig Windowed(int hours = 72)
            => new PiggyConfig { Enabled = true, CycleHours = hours, MinBreakPercent = 100m };

        [Fact]
        public void AWindowStillRunningIsNotExpired()
            => Assert.False(PiggyMath.IsExpired(Now, Now.AddHours(1)));

        [Fact]
        public void AWindowPastItsEndIsExpired()
            => Assert.True(PiggyMath.IsExpired(Now, Now.AddSeconds(-1)));

        [Fact]
        public void NoWindowMeansNoDeadline()
        {
            // The normal state, not an edge case: a bank that is still filling — or one that filled while the player
            // was away and hasn't been shown to them yet — has no clock at all.
            Assert.False(PiggyMath.IsExpired(Now, null));
        }

        [Fact]
        public void TheWindowEndsAfterTheConfiguredHours()
            => Assert.Equal(Now.AddHours(72), PiggyMath.WindowEnd(Now, Windowed()));

        [Fact]
        public void NoWindowEndWhenExpiryIsOff()
            => Assert.Null(PiggyMath.WindowEnd(Now, Windowed(hours: 0)));

        [Fact]
        public void SecondsLeftCountsDownAndFloorsAtZero()
        {
            Assert.Equal(3_600, PiggyMath.SecondsLeft(Now, Now.AddHours(1)));
            Assert.Equal(0, PiggyMath.SecondsLeft(Now, Now.AddHours(-1)));
            Assert.Equal(0, PiggyMath.SecondsLeft(Now, null));
        }

        // ---- the bar ----

        [Theory]
        [InlineData(0, 250_000, 0f)]
        [InlineData(125_000, 250_000, 0.5f)]
        [InlineData(250_000, 250_000, 1f)]
        [InlineData(300_000, 250_000, 1f)]   // over-full still draws full rather than overflowing the bar
        [InlineData(1_000, 0, 0f)]           // no capacity: no bar, no divide by zero
        public void ThePercentIsClamped(decimal amount, decimal max, float expected)
            => Assert.Equal(expected, PiggyMath.Percent01(amount, max), 3);
    }
}
