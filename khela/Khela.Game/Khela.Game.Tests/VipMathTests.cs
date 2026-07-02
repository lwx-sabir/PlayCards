using System.Collections.Generic;
using Khela.Common.Leaderboards;
using Khela.Game.Services.Vip;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the VIP tier math (Progression Spec §3): flat-×1 SP accrual, the level-gated Bronze floor, the
    /// SP + USD-spend-floor band resolution (spend gates the upper tiers), the benefit multiplier (never on SP),
    /// and the scalar runtime overlay. Pure math — no DB, no clock.
    /// </summary>
    public class VipMathTests
    {
        private static VipConfig Cfg() => new();   // locked §3 defaults (entry L20, /50 SP, the tier arrays)

        // ---- SP accrual (flat ×1) ----

        [Theory]
        [InlineData(5000, 100)]   // floor(5000/50)
        [InlineData(49, 0)]       // floor(49/50)
        [InlineData(0, 0)]
        [InlineData(-100, 0)]
        public void SpFromWager_IsFloorOfWagerOverDivisor(double wager, long expected)
            => Assert.Equal(expected, VipMath.SpFromWager((decimal)wager, Cfg()));

        [Theory]
        [InlineData(1, 100)]      // $1 × 100
        [InlineData(4.99, 499)]
        [InlineData(0, 0)]
        public void SpFromPurchase_IsFloorOfUsdTimesRate(double usd, long expected)
            => Assert.Equal(expected, VipMath.SpFromPurchase((decimal)usd, Cfg()));

        // ---- band resolution ----

        [Fact]
        public void ResolveBand_BelowEntryLevel_IsNone_EvenWithHugeSp()
            => Assert.Equal(VipTier.None, VipMath.ResolveBand(999_999_999, 99_999m, level: 19, Cfg()));

        [Fact]
        public void ResolveBand_AtEntryLevel_NoSp_IsBronzeFloor()
            => Assert.Equal(VipTier.Bronze, VipMath.ResolveBand(0, 0m, level: 20, Cfg()));

        [Theory]
        [InlineData(50_000, VipTier.Silver)]
        [InlineData(250_000, VipTier.Gold)]
        public void ResolveBand_SpOnlyBands_NeedNoSpendFloor(long sp, VipTier expected)
            => Assert.Equal(expected, VipMath.ResolveBand(sp, 0m, level: 50, Cfg()));

        [Fact]
        public void ResolveBand_UpperBands_RequireSpendFloor_ElseCapAtGold()
        {
            // 150M SP qualifies for Black Diamond by SP alone, but $0 spend fails every spend-gated floor (Platinum+),
            // so it caps at the highest SP-only band — Gold.
            Assert.Equal(VipTier.Gold, VipMath.ResolveBand(150_000_000, 0m, level: 100, Cfg()));
            // Platinum SP bar + its $30 spend floor → Platinum.
            Assert.Equal(VipTier.Platinum, VipMath.ResolveBand(1_250_000, 30m, level: 100, Cfg()));
            // Full apex: 150M SP + $4,000 spend → Black Diamond.
            Assert.Equal(VipTier.BlackDiamond, VipMath.ResolveBand(150_000_000, 4_000m, level: 100, Cfg()));
        }

        // ---- multiplier (benefit track only) + badge ----

        [Theory]
        [InlineData(VipTier.None, 0, 1.00)]            // no tier bonus, no VIP level
        [InlineData(VipTier.Bronze, 0, 1.01)]          // +1% tier
        [InlineData(VipTier.Gold, 3, 1.55)]            // +5% tier + +50% VIP3
        [InlineData(VipTier.BlackDiamond, 10, 2.85)]   // +15% tier + +170% VIP10 = +185% comp
        public void ComboMultiplier_AdditiveTierPlusVipLevel(VipTier tier, int vipLevel, double expected)
            => Assert.Equal((decimal)expected, VipMath.ComboMultiplier(tier, vipLevel, Cfg()));

        [Fact]
        public void VipProgressFromSp_ScalesBySpAndTierFactor()
        {
            Assert.Equal(1000, VipMath.VipProgressFromSp(1000, VipTier.Bronze, Cfg()));        // factor 1.0
            Assert.Equal(3000, VipMath.VipProgressFromSp(1000, VipTier.BlackDiamond, Cfg()));  // factor 3.0
            Assert.Equal(0, VipMath.VipProgressFromSp(1000, VipTier.None, Cfg()));             // factor 0 (unranked earns none)
        }

        [Fact]
        public void ApplyVipLevelUps_CrossesThresholds_AndCapsAt10()
        {
            var c = Cfg();
            var (prog, lvl) = VipMath.ApplyVipLevelUps(0, 0, 5_000_000, c);   // VIP1 threshold = 5M
            Assert.Equal(1, lvl);
            Assert.Equal(0, prog);
            var (_, capped) = VipMath.ApplyVipLevelUps(0, 0, long.MaxValue / 2, c);
            Assert.Equal(10, capped);
        }

        [Theory]
        [InlineData(VipTier.None, false)]
        [InlineData(VipTier.Bronze, false)]   // Bronze is the floor, NOT a badge
        [InlineData(VipTier.Silver, true)]
        [InlineData(VipTier.BlackDiamond, true)]
        public void HasBadge_OnlySilverAndUp(VipTier tier, bool expected)
            => Assert.Equal(expected, VipMath.HasBadge(tier));

        // ---- runtime overlay (scalars only; tier arrays + Enabled pass through) ----

        [Fact]
        public void Overlay_NullOrEmpty_ReturnsBase()
        {
            var b = Cfg();
            Assert.Same(b, VipConfig.Overlay(b, null));
            Assert.Same(b, VipConfig.Overlay(b, new Dictionary<string, string>()));
        }

        [Fact]
        public void Overlay_AppliesScalars_KeepsArrays_AndDoesNotOverrideEnabled()
        {
            var b = Cfg();
            var e = VipConfig.Overlay(b, new Dictionary<string, string>
            {
                ["Vip:VipEntryLevel"] = "30",
                ["Vip:SpChipsPerPoint"] = "25",
                ["Vip:BadgeWindowDays"] = "14",
                ["Vip:Enabled"] = "false",     // must NOT take effect
            });
            Assert.Equal(30, e.VipEntryLevel);
            Assert.Equal(25m, e.SpChipsPerPoint);
            Assert.Equal(14, e.BadgeWindowDays);
            Assert.True(e.Enabled);                        // master switch not overridable
            Assert.Same(b.SpThresholds, e.SpThresholds);   // per-tier arrays pass through unchanged
        }
    }
}
