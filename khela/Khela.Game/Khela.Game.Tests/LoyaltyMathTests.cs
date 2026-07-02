using System.Collections.Generic;
using Khela.Game.Services.Loyalty;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the Loyalty earn math (Progression Spec §4): LP = floor(cleanWager / LpChipsPerPoint) × the VIP
    /// benefit multiplier, the dormant purchase drip (off until LpPerUsd &gt; 0), and the scalar runtime overlay
    /// (catalog passes through). Pure math — no DB, no clock.
    /// </summary>
    public class LoyaltyMathTests
    {
        private static LoyaltyConfig Cfg() => new();   // LpChipsPerPoint = 100, LpPerUsd = 0 (drip off)

        [Theory]
        [InlineData(10000, 1.0, 100)]   // floor(10000/100) × 1.0
        [InlineData(10000, 1.3, 130)]   // × Silver multiplier
        [InlineData(10000, 4.5, 450)]   // × Black Diamond multiplier
        [InlineData(50, 1.0, 0)]        // below one point
        [InlineData(0, 2.0, 0)]
        [InlineData(-100, 2.0, 0)]
        public void LpFromWager_IsFloorOverDivisorTimesMultiplier(double wager, double mult, long expected)
            => Assert.Equal(expected, LoyaltyMath.LpFromWager((decimal)wager, (decimal)mult, Cfg()));

        [Fact]
        public void LpFromWager_ZeroOrNegativeMultiplier_TreatedAsOne()
        {
            Assert.Equal(100, LoyaltyMath.LpFromWager(10000m, 0m, Cfg()));
            Assert.Equal(100, LoyaltyMath.LpFromWager(10000m, -3m, Cfg()));
        }

        [Fact]
        public void LpFromPurchase_DormantByDefault()
            => Assert.Equal(0, LoyaltyMath.LpFromPurchase(100m, 2.0m, Cfg()));   // LpPerUsd = 0 → off until IAP

        [Fact]
        public void LpFromPurchase_WhenEnabled_AppliesRateAndMultiplier()
        {
            var c = new LoyaltyConfig { LpPerUsd = 10m };   // others keep defaults
            Assert.Equal(20, LoyaltyMath.LpFromPurchase(1m, 2.0m, c));   // floor(1×10) × 2
        }

        [Fact]
        public void Overlay_AppliesScalars_KeepsCatalog_AndEnabled()
        {
            var b = Cfg();
            var e = LoyaltyConfig.Overlay(b, new Dictionary<string, string>
            {
                ["Loyalty:LpChipsPerPoint"] = "50",
                ["Loyalty:Enabled"] = "false",   // must NOT take effect
            });
            Assert.Equal(50m, e.LpChipsPerPoint);
            Assert.True(e.Enabled);              // master switch not overridable
            Assert.Same(b.Catalog, e.Catalog);  // catalog passes through unchanged
        }
    }
}
