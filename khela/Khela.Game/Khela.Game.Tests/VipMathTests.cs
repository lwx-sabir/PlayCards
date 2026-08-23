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

        // Money no longer buys SP at all (docs/VIP_SPEC.md §2): a purchase credits VIP-P, which is a different ladder.
        // Anything it grants toward status now has to be an explicit `Sp` line on the product.

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
        public void ResolveBand_ShippedDefaults_HaveNoSpendGate_SoPlayAloneReachesTheApex()
        {
            // The badge tier is an ACTIVITY ladder (docs/VIP_SPEC.md §2) — money buys VIP-P, never the badge. So the
            // shipped floors are all $0 and 150M SP reaches Black Diamond on play alone.
            Assert.Equal(VipTier.BlackDiamond, VipMath.ResolveBand(150_000_000, 0m, level: 100, Cfg()));
            Assert.Equal(VipTier.Platinum, VipMath.ResolveBand(1_250_000, 0m, level: 100, Cfg()));
        }

        [Fact]
        public void ResolveBand_AdminSetSpendFloor_StillGatesTheBandItGuards()
        {
            // The mechanism survives for an admin who wants it back: a floor on Platinum+ caps a $0 player at Gold.
            var gated = new VipConfig { SpendFloorsUsd = new[] { 0m, 0m, 0m, 0m, 30m, 250m, 1_000m, 4_000m } };
            Assert.Equal(VipTier.Gold, VipMath.ResolveBand(150_000_000, 0m, level: 100, gated));
            Assert.Equal(VipTier.Platinum, VipMath.ResolveBand(1_250_000, 30m, level: 100, gated));
            Assert.Equal(VipTier.BlackDiamond, VipMath.ResolveBand(150_000_000, 4_000m, level: 100, gated));
        }

        // ---- multiplier (benefit track only) + badge ----

        [Theory]
        [InlineData(VipTier.None, 0, 1.00)]            // no tier bonus, no VIP level
        [InlineData(VipTier.Bronze, 0, 1.01)]          // +1% tier
        [InlineData(VipTier.Gold, 3, 1.55)]            // +5% tier + +50% VIP3
        [InlineData(VipTier.BlackDiamond, 10, 2.85)]   // +15% tier + +170% VIP10 = +185% comp
        public void ComboMultiplier_AdditiveTierPlusVipLevel(VipTier tier, int vipLevel, double expected)
            => Assert.Equal((decimal)expected, VipMath.ComboMultiplier(tier, vipLevel, Cfg()));

        // ---- VIP level: the band of what was BOUGHT (docs/VIP_SPEC.md §4) ----

        [Theory]
        [InlineData(0, 0)]
        [InlineData(299, 0)]          // one VIP-P short of the door
        [InlineData(300, 1)]          // the Starter Pack
        [InlineData(999, 1)]
        [InlineData(1_000, 2)]
        [InlineData(300_000, 10)]
        [InlineData(50_000_000, 10)]  // nothing above the top rung exists to climb to
        public void LevelFromPoints_IsTheHighestBarTheWindowClears(long points, int expected)
            => Assert.Equal(expected, VipMath.LevelFromPoints(points, Cfg()));

        [Fact]
        public void LevelFromPoints_IgnoresPlay_ThereIsNoGrind()
        {
            // The only input is VIP-P. A player with a huge SP balance and no purchases stands at VIP 0 — the whole
            // point of the redesign: play climbs the badge, money climbs VIP.
            Assert.Equal(0, VipMath.LevelFromPoints(0, Cfg()));
        }

        [Fact]
        public void EffectiveLevel_TakesTheHold_OnlyWhileItIsLive_AndNeverBelowTheBand()
        {
            var c = Cfg();
            var now = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

            // window says 1, a live hold says 5 → 5 (this is the buffer a purchase buys)
            Assert.Equal(5, VipMath.EffectiveLevel(300, 5, now.AddDays(10), now, c));
            // the same hold, expired → the window alone
            Assert.Equal(1, VipMath.EffectiveLevel(300, 5, now.AddDays(-1), now, c));
            // no hold at all → the window alone
            Assert.Equal(1, VipMath.EffectiveLevel(300, 0, null, now, c));
            // a STALE low hold never drags a player down: the band wins whenever it is higher
            Assert.Equal(3, VipMath.EffectiveLevel(2_500, 1, now.AddDays(10), now, c));
            // and a hold above the ladder is clamped to the ladder
            Assert.Equal(10, VipMath.EffectiveLevel(0, 99, now.AddDays(10), now, c));
        }

        [Fact]
        public void ShouldRearmHold_RefusesACheapPackUnderALiveHold()
        {
            var now = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
            var live = now.AddDays(20);

            // THE exploit this rule exists for: holding VIP 10, buying a pack that only stands at VIP 1.
            Assert.False(VipMath.ShouldRearmHold(band: 1, heldLevel: 10, heldThrough: live, now: now));
            // Reaching it again, or beating it, re-arms.
            Assert.True(VipMath.ShouldRearmHold(band: 10, heldLevel: 10, heldThrough: live, now: now));
            Assert.True(VipMath.ShouldRearmHold(band: 11, heldLevel: 10, heldThrough: live, now: now));
            // A lapsed hold protects nothing, so any credit re-snapshots against the window.
            Assert.True(VipMath.ShouldRearmHold(band: 1, heldLevel: 10, heldThrough: now.AddDays(-1), now: now));
            Assert.True(VipMath.ShouldRearmHold(band: 1, heldLevel: 10, heldThrough: null, now: now));
            // No hold at all: the first purchase always sets one.
            Assert.True(VipMath.ShouldRearmHold(band: 1, heldLevel: 0, heldThrough: null, now: now));
        }

        [Fact]
        public void ExpectedReturn_IsZeroWhenTheDailyCapIsOff_AndRisesWithTheTwoPercentages()
        {
            var c = Cfg();
            // 0 = OFF, the exchange convention — a level with no cap pays nothing, so it costs nothing.
            var off = new VipConfig { VipWinBonusPct = new[] { 0m, 0.5m }, VipLossRebatePct = new[] { 0m, 0.5m }, VipDailyCapChips = new[] { 0m, 0m } };
            Assert.Equal(0m, VipMath.ExpectedReturnOfHandle(1, off, VipMath.ModelHouseEdge));

            // The shipped ladder must not pay more than the house earns at ANY rung — that is the +EV line.
            for (int lvl = 1; lvl <= VipMath.TopVipLevel(c); lvl++)
                Assert.True(VipMath.ExpectedReturnOfHandle(lvl, c, VipMath.ModelHouseEdge) < VipMath.ModelHouseEdge,
                    $"VIP {lvl} returns more than the house edge — that level pays players to play.");

            // …and it is monotone in the two percentages, so the admin column moves the way the numbers do.
            Assert.True(VipMath.ExpectedReturnOfHandle(10, c, VipMath.ModelHouseEdge)
                      > VipMath.ExpectedReturnOfHandle(1, c, VipMath.ModelHouseEdge));
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
