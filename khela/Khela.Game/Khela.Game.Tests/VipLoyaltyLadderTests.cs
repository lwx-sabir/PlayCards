using System.Collections.Generic;
using System.Linq;
using Khela.Game.Services.Loyalty;
using Khela.Game.Services.Vip;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// The VIP ladders and the LP store became admin-editable, so the parsers are the new guardrail: a ladder the admin
    /// mis-saves must fall back WHOLE to the built-in one rather than half-apply — a half-applied ladder ranks players by
    /// numbers nobody chose. Pure, no I/O.
    /// </summary>
    public class VipLoyaltyLadderTests
    {
        private static VipConfig Base() => new VipConfig();

        private static List<VipConfig.TierRow> GoodTiers() => VipConfig.TierRows(Base());
        private static List<VipConfig.LevelRow> GoodLevels() => VipConfig.LevelRows(Base());

        // ---- shape ----

        [Fact]
        public void TierRows_AreTheEightTiers_AndLevelRowsStartAtLevelOne()
        {
            var tiers = GoodTiers();
            Assert.Equal(8, tiers.Count);
            Assert.Equal(150_000_000L, tiers[7].SpThreshold);  // BlackDiamond
            Assert.Equal(0m, tiers[7].SpendFloorUsd);          // spend floors retired: money buys VIP-P, not status
            Assert.Equal(6, tiers[7].ResetTo);                 // Black Diamond falls to Royal Diamond at the season roll

            var levels = GoodLevels();
            Assert.Equal(10, levels.Count);
            Assert.Equal(300L, levels[0].PointsRequired);      // index 0 == VIP 1 == what the Starter Pack carries
            Assert.Equal(0.20m, levels[0].BonusPct);
            Assert.Equal(300_000L, levels[9].PointsRequired);
            Assert.Equal(13_000L, levels[9].MaintainLp);
            Assert.Equal(90, levels[9].HoldDays);
        }

        [Fact]
        public void Ladders_RoundTrip()
        {
            var tiers = VipConfig.ParseTiers(VipConfig.SerializeTiers(GoodTiers()), Base());
            Assert.Equal(Base().SpThresholds, tiers.SpThresholds);
            Assert.Equal(Base().SpendFloorsUsd, tiers.SpendFloorsUsd);
            Assert.Equal(Base().TierBonusPct, tiers.TierBonusPct);
            Assert.Equal(Base().TierResetTo, tiers.TierResetTo);

            var levels = VipConfig.ParseLevels(VipConfig.SerializeLevels(GoodLevels()), Base());
            Assert.Equal(Base().VipPointsRequired, levels.VipPointsRequired);
            Assert.Equal(Base().VipLevelBonusPct, levels.VipLevelBonusPct);
            Assert.Equal(Base().VipHoldDays, levels.VipHoldDays);
            Assert.Equal(Base().VipWinBonusPct, levels.VipWinBonusPct);
            Assert.Equal(Base().VipLossRebatePct, levels.VipLossRebatePct);
            Assert.Equal(Base().VipDailyCapChips, levels.VipDailyCapChips);
            Assert.Equal(Base().VipMaintainLpCost, levels.VipMaintainLpCost);
        }

        [Fact]
        public void Overlay_AppliesASavedLadder_AndTheEngineUsesIt()
        {
            var tiers = GoodTiers();
            tiers[2] = new VipConfig.TierRow { SpThreshold = 10_000, SpendFloorUsd = 0m, BonusPct = 0.05m, ResetTo = 1 };   // Silver
            var levels = GoodLevels();
            levels[0] = new VipConfig.LevelRow { PointsRequired = 100, BonusPct = 0.5m, MaintainLp = 50, HoldDays = 14 };   // VIP 1

            var cfg = VipConfig.Overlay(Base(), new Dictionary<string, string>
            {
                ["Vip:Tiers"] = VipConfig.SerializeTiers(tiers),
                ["Vip:Levels"] = VipConfig.SerializeLevels(levels),
            });

            Assert.Equal(10_000L, cfg.SpThresholds[2]);
            Assert.Equal(100L, VipMath.PointsRequired(1, cfg));
            Assert.Equal(50L, VipMath.MaintainLpCost(1, cfg));
            Assert.Equal(14, VipMath.HoldDays(1, cfg));
            // …and the band honours the new, cheaper rung: 100 VIP-P is VIP 1 where the built-in ladder wants 300.
            Assert.Equal(1, VipMath.LevelFromPoints(100, cfg));
            Assert.Equal(0, VipMath.LevelFromPoints(100, Base()));
        }

        // ---- fail whole ----

        [Fact]
        public void ATierLadderThatIsWrong_FallsBackWhole()
        {
            var b = Base();
            void Same((long[] Sp, decimal[] Floors, decimal[] Bonus, int[] Resets) got)
                => Assert.Equal(b.SpThresholds, got.Sp);

            Same(VipConfig.ParseTiers("", b));
            Same(VipConfig.ParseTiers("{ not json", b));

            var short7 = GoodTiers().Take(7).ToList();                       // the tier enum is 8 wide
            Same(VipConfig.ParseTiers(VipConfig.SerializeTiers(short7), b));

            var descending = GoodTiers();
            descending[5] = new VipConfig.TierRow { SpThreshold = 1, SpendFloorUsd = 0m, BonusPct = 0.1m, ResetTo = 4 };
            Same(VipConfig.ParseTiers(VipConfig.SerializeTiers(descending), b));   // a ladder must not go down

            var negative = GoodTiers();
            negative[3] = new VipConfig.TierRow { SpThreshold = 100, SpendFloorUsd = -1m, BonusPct = 0m, ResetTo = 2 };
            Same(VipConfig.ParseTiers(VipConfig.SerializeTiers(negative), b));
        }

        [Fact]
        public void ALevelLadderThatIsWrong_FallsBackWhole()
        {
            var b = Base();
            Assert.Equal(b.VipPointsRequired, VipConfig.ParseLevels("", b).VipPointsRequired);
            Assert.Equal(b.VipPointsRequired, VipConfig.ParseLevels("[]", b).VipPointsRequired);

            // A zero bar would hand a paid level to a player who never spent a cent.
            var zero = GoodLevels();
            zero[4] = new VipConfig.LevelRow { PointsRequired = 0, BonusPct = 1m, MaintainLp = 10, HoldDays = 30 };
            Assert.Equal(b.VipPointsRequired, VipConfig.ParseLevels(VipConfig.SerializeLevels(zero), b).VipPointsRequired);

            // Bars that do not RISE would make two levels the same purchase.
            var flat = GoodLevels();
            flat[3] = new VipConfig.LevelRow { PointsRequired = flat[2].PointsRequired, BonusPct = 1m, MaintainLp = 10, HoldDays = 30 };
            Assert.Equal(b.VipPointsRequired, VipConfig.ParseLevels(VipConfig.SerializeLevels(flat), b).VipPointsRequired);

            // A negative rebate % is refused whole rather than clamped — a ladder half-applied is a ladder nobody set.
            var negative = GoodLevels();
            negative[1] = new VipConfig.LevelRow { PointsRequired = 900, BonusPct = 0.3m, MaintainLp = 10, HoldDays = 30, LossRebatePct = -0.1m };
            Assert.Equal(b.VipPointsRequired, VipConfig.ParseLevels(VipConfig.SerializeLevels(negative), b).VipPointsRequired);
        }

        [Fact]
        public void ALevelLadderSavedBeforeTheVipPRedesign_KeepsTheBuiltInColumns()
        {
            // The exact shape a document saved by the OLD editor has: comp % and maintain LP, and none of the columns the
            // VIP-P redesign added. Reading those as 0 would set every bar to nothing (the top level for free) and every
            // daily cap to off, so a missing column falls back to the built-in rung for the SAME level.
            var b = Base();
            var old = "[{\"BonusPct\":0.2,\"MaintainLp\":500},{\"BonusPct\":0.35,\"MaintainLp\":800}]";
            var parsed = VipConfig.ParseLevels(old, b);
            Assert.Equal(300L, parsed.VipPointsRequired[1]);
            Assert.Equal(1_000L, parsed.VipPointsRequired[2]);
            Assert.Equal(30, parsed.VipHoldDays[1]);
            Assert.Equal(b.VipDailyCapChips[1], parsed.VipDailyCapChips[1]);
            Assert.Equal(0.2m, parsed.VipLevelBonusPct[1]);   // …and what the admin DID save still applies
        }

        [Fact]
        public void TheLevelCap_FollowsTheLADDER_NotALiteralTen()
        {
            // A ladder is whatever the admin saved. Three rungs: a player must stop at 3, and the top must read as the top.
            var three = GoodLevels().Take(3).ToList();
            var cfg = VipConfig.Overlay(Base(), new Dictionary<string, string> { ["Vip:Levels"] = VipConfig.SerializeLevels(three) });
            Assert.Equal(4, cfg.VipPointsRequired.Length);            // [0] unused + 3 levels
            Assert.Equal(3, VipMath.TopVipLevel(cfg));
            Assert.Equal(2_500L, VipMath.PointsRequired(3, cfg));
            Assert.Equal(long.MaxValue, VipMath.PointsRequired(4, cfg));   // out of range = unreachable

            // Enough VIP-P for ten levels stops at the top rung rather than banding off the end of the array.
            Assert.Equal(3, VipMath.LevelFromPoints(500_000, cfg));

            // A LONGER ladder is climbable past 10 — the old literal cap would have stranded every rung above it.
            var twelve = GoodLevels().ToList();
            twelve.Add(new VipConfig.LevelRow { PointsRequired = 500_000, BonusPct = 1.8m, MaintainLp = 15_000, HoldDays = 90 });
            twelve.Add(new VipConfig.LevelRow { PointsRequired = 800_000, BonusPct = 1.9m, MaintainLp = 17_000, HoldDays = 90 });
            var big = VipConfig.Overlay(Base(), new Dictionary<string, string> { ["Vip:Levels"] = VipConfig.SerializeLevels(twelve) });
            Assert.Equal(12, VipMath.TopVipLevel(big));
            Assert.Equal(12, VipMath.LevelFromPoints(800_000, big));
            Assert.Equal(1.9m, VipMath.VipLevelBonus(12, big));
        }

        // ---- LP store ----

        [Fact]
        public void LpCatalog_RoundTrips_AndRefusesWhole()
        {
            var b = new LoyaltyConfig();
            var json = LoyaltyConfig.SerializeCatalog(b.Catalog);
            var back = LoyaltyConfig.ParseCatalog(json, b.Catalog);
            Assert.Equal(3, back.Length);
            Assert.Equal("chips_1k", back[0].Id);
            Assert.Equal(10L, back[0].CostLp);
            Assert.Equal(1_000m, back[0].ChipAmount);

            Assert.Same(b.Catalog, LoyaltyConfig.ParseCatalog("", b.Catalog));
            Assert.Same(b.Catalog, LoyaltyConfig.ParseCatalog("{ not json", b.Catalog));
            Assert.Same(b.Catalog, LoyaltyConfig.ParseCatalog("[]", b.Catalog));   // an empty shop reads as broken

            var bad = new[] { new LoyaltyStoreItem { Id = "x", CostLp = 0, ChipAmount = 100m } };
            Assert.Same(b.Catalog, LoyaltyConfig.ParseCatalog(LoyaltyConfig.SerializeCatalog(bad), b.Catalog));

            var dupes = new[]
            {
                new LoyaltyStoreItem { Id = "x", CostLp = 10, ChipAmount = 100m },
                new LoyaltyStoreItem { Id = "X", CostLp = 20, ChipAmount = 200m },
            };
            Assert.Same(b.Catalog, LoyaltyConfig.ParseCatalog(LoyaltyConfig.SerializeCatalog(dupes), b.Catalog));
        }

        [Fact]
        public void LpCatalog_IsOverlaid_AndTheCompRateIsWhatTheTableSays()
        {
            var b = new LoyaltyConfig();
            var priced = new[] { new LoyaltyStoreItem { Id = "chips_1k", Name = "1,000 Chips", Kind = "chips", CostLp = 1_000, ChipAmount = 1_000m } };
            var cfg = LoyaltyConfig.Overlay(b, new Dictionary<string, string>
            {
                ["Loyalty:Catalog"] = LoyaltyConfig.SerializeCatalog(priced),
            });
            Assert.Single(cfg.Catalog);
            Assert.Equal(1_000L, cfg.Catalog[0].CostLp);

            // comp = chips ÷ (costLp × chipsPerLp). The built-in list is 1,000 ÷ (10 × 100) = 100% — a loop.
            // Re-priced at 1,000 LP it is 1,000 ÷ (1,000 × 100) = 1%, which is what "~1% comp" meant.
            decimal Comp(LoyaltyStoreItem i, decimal chipsPerLp) => i.ChipAmount / (i.CostLp * chipsPerLp);
            Assert.Equal(1m, Comp(b.Catalog[0], b.LpChipsPerPoint));
            Assert.Equal(0.01m, Comp(cfg.Catalog[0], cfg.LpChipsPerPoint));
        }
    }
}
