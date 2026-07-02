using System.Collections.Generic;
using System.Linq;
using Khela.Game.Services.Progression;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the progression curve + accrual math (Progression Spec System A): super-linear XpToNext, the
    /// tiered soft-floor wager XP (1k floor ≤ L3, 5k above, 0.2× below floor, win bonus), the multi-level
    /// carry-over loop, and the level-up reward. Pure math — no DB, no clock.
    /// </summary>
    public class ProgressionMathTests
    {
        private static ProgressionConfig Cfg() => new();   // spec defaults

        // ---- curve ----

        [Theory]
        [InlineData(1, 150)]    // round_to_50(150 * 1^1.6)
        [InlineData(2, 450)]    // round_to_50(150 * 2^1.6 = 454.5)
        public void XpToNext_FollowsSuperLinearCurveRoundedTo50(int level, long expected)
        {
            Assert.Equal(expected, ProgressionMath.XpToNext(level, 150, 1.6));
        }

        [Fact]
        public void XpToNext_StrictlyIncreasesAndIsAlwaysPositive()
        {
            long prev = 0;
            for (int l = 1; l <= 60; l++)
            {
                var x = ProgressionMath.XpToNext(l, 150, 1.6);
                Assert.True(x > 0, $"L{l} must need positive XP");
                Assert.True(x >= prev, $"L{l} must not need less than L{l - 1}");
                prev = x;
            }
        }

        [Fact]
        public void XpToNext_FloorsAtFifty_NeverZero()
        {
            // A tiny base used to round the threshold to 0 → the level-up loop stalled (its `need > 0` guard) and
            // into-level XP grew unbounded (the "250 / 150" overfill bug). The threshold must never drop below 50.
            Assert.Equal(50, ProgressionMath.XpToNext(1, 10, 1.6));   // 10*1^1.6/50 = 0.2 → would round to 0
            Assert.True(ProgressionMath.XpToNext(1, 0, 1.6) >= 50);
        }

        // ---- tiered soft floor ----

        [Fact]
        public void RawXp_AtOrAboveFloor_PaysFullRate()
        {
            // L2 (early, floor 1000): a 1000 clean bet → floor(1000/10) = 100 XP, win bonus +10.
            Assert.Equal(100, ProgressionMath.RawXp(1000m, level: 2, win: false, Cfg()));
            Assert.Equal(110, ProgressionMath.RawXp(1000m, level: 2, win: true, Cfg()));
        }

        [Fact]
        public void RawXp_BelowFloor_PaysReducedRate_NotZero()
        {
            // L5 (late, floor 5000): a 1000 bet is below floor → 0.2× → floor(floor(1000/10) * 0.2) = 20.
            Assert.Equal(20, ProgressionMath.RawXp(1000m, level: 5, win: false, Cfg()));
            // ...and the SAME 1000 bet at L2 is at/above the early 1000 floor → full 100.
            Assert.Equal(100, ProgressionMath.RawXp(1000m, level: 2, win: false, Cfg()));
        }

        [Fact]
        public void RawXp_HighTierAtFloor_PaysFull()
        {
            // L5, 5000 bet (== late floor) → floor(5000/10) = 500.
            Assert.Equal(500, ProgressionMath.RawXp(5000m, level: 5, win: false, Cfg()));
        }

        [Fact]
        public void RawXp_ZeroOrNegativeWager_IsZero()
        {
            Assert.Equal(0, ProgressionMath.RawXp(0m, 1, true, Cfg()));
            Assert.Equal(0, ProgressionMath.RawXp(-100m, 1, true, Cfg()));
        }

        // ---- level-up loop ----

        [Fact]
        public void ApplyLevelUps_CarriesRemainderIntoNextLevel()
        {
            // L1 needs 150. Grant 200 from L1/0 → reach L2 with 50 carry, one level crossed.
            var (exp, level, crossed) = ProgressionMath.ApplyLevelUps(0, 1, 200, 150, 1.6);
            Assert.Equal(2, level);
            Assert.Equal(50, exp);
            Assert.Equal(new[] { 2 }, crossed.ToArray());
        }

        [Fact]
        public void ApplyLevelUps_CanCrossMultipleLevelsInOneGrant_AndReportsEach()
        {
            // L1→2 needs 150, L2→3 needs 450. Grant 650 from L1/0 → 150 + 450 = 600 consumed, reach L3 with 50.
            var (exp, level, crossed) = ProgressionMath.ApplyLevelUps(0, 1, 650, 150, 1.6);
            Assert.Equal(3, level);
            Assert.Equal(50, exp);
            Assert.Equal(new[] { 2, 3 }, crossed.ToArray());
        }

        [Fact]
        public void ApplyLevelUps_BelowThreshold_JustAccumulates()
        {
            var (exp, level, crossed) = ProgressionMath.ApplyLevelUps(40, 1, 50, 150, 1.6);
            Assert.Equal(1, level);
            Assert.Equal(90, exp);
            Assert.Empty(crossed);
        }

        [Fact]
        public void ApplyLevelUps_WithZeroGrant_NormalizesDriftedLevel()
        {
            // Stored drift: Level 1 holding 250 into-level XP (>= XpToNext(1)=150) — e.g. after a curve retune.
            // Re-applying 0 XP carries the excess into the correct level (L2 with 100) — the display normalization
            // GetMyProgressionAsync relies on, so the bar never overfills.
            var (exp, level, _) = ProgressionMath.ApplyLevelUps(250, 1, 0, 150, 1.6);
            Assert.Equal(2, level);
            Assert.Equal(100, exp);
        }

        // ---- level reward ----

        [Theory]
        [InlineData(2, 20000)]
        [InlineData(3, 30000)]
        [InlineData(10, 100000)]
        public void LevelUpReward_IsBasePerLevelRoundedTo100(int level, long expected)
        {
            Assert.Equal(expected, ProgressionMath.LevelUpReward(level, 10000));
        }

        // ---- runtime overrides (admin dashboard → Redis "khela:settings" → engine) ----

        [Fact]
        public void Overlay_NullOrEmpty_ReturnsBaseUnchanged()
        {
            var b = Cfg();
            Assert.Same(b, ProgressionMath.Overlay(b, null));
            Assert.Same(b, ProgressionMath.Overlay(b, new Dictionary<string, string>()));
        }

        [Fact]
        public void Overlay_ValidOverrides_AreApplied_OthersKeepBase()
        {
            var b = Cfg();
            var e = ProgressionMath.Overlay(b, new Dictionary<string, string>
            {
                ["Progression:XpChipsPerPoint"] = "100",
                ["Progression:MaxWagerPerBet"] = "5000",
                ["Progression:DailyXpCap"] = "99000",
                ["Progression:XpExp"] = "1.9",
            });
            Assert.Equal(100m, e.XpChipsPerPoint);
            Assert.Equal(5000m, e.MaxWagerPerBet);
            Assert.Equal(99000L, e.DailyXpCap);
            Assert.Equal(1.9, e.XpExp);
            Assert.Equal(b.LvlupBase, e.LvlupBase);   // untouched key keeps the base value
        }

        [Fact]
        public void Overlay_UnparseableOrEmpty_KeepsBase()
        {
            var b = Cfg();
            var e = ProgressionMath.Overlay(b, new Dictionary<string, string>
            {
                ["Progression:XpChipsPerPoint"] = "abc",
                ["Progression:DailyXpCap"] = "",
            });
            Assert.Equal(b.XpChipsPerPoint, e.XpChipsPerPoint);
            Assert.Equal(b.DailyXpCap, e.DailyXpCap);
        }

        [Fact]
        public void Overlay_DoesNotOverrideEnabledMasterSwitch()
        {
            var b = Cfg();   // Enabled = true (default)
            var e = ProgressionMath.Overlay(b, new Dictionary<string, string> { ["Progression:Enabled"] = "false" });
            Assert.True(e.Enabled);
        }
    }
}
