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
            Assert.Equal(0m, tiers[0].Factor);                    // None grinds nothing
            Assert.Equal(150_000_000L, tiers[7].SpThreshold);      // BlackDiamond
            Assert.Equal(4_000m, tiers[7].SpendFloorUsd);

            var levels = GoodLevels();
            Assert.Equal(10, levels.Count);
            Assert.Equal(5_000_000L, levels[0].Threshold);         // index 0 == VIP 1
            Assert.Equal(0.20m, levels[0].BonusPct);
            Assert.Equal(28_000_000L, levels[9].Threshold);
            Assert.Equal(13_000L, levels[9].MaintainLp);
        }

        [Fact]
        public void Ladders_RoundTrip()
        {
            var tiers = VipConfig.ParseTiers(VipConfig.SerializeTiers(GoodTiers()), Base());
            Assert.Equal(Base().SpThresholds, tiers.SpThresholds);
            Assert.Equal(Base().SpendFloorsUsd, tiers.SpendFloorsUsd);
            Assert.Equal(Base().TierBonusPct, tiers.TierBonusPct);
            Assert.Equal(Base().TierFactors, tiers.TierFactors);

            var levels = VipConfig.ParseLevels(VipConfig.SerializeLevels(GoodLevels()), Base());
            Assert.Equal(Base().VipLevelThresholds, levels.VipLevelThresholds);
            Assert.Equal(Base().VipLevelBonusPct, levels.VipLevelBonusPct);
            Assert.Equal(Base().VipMaintainLpCost, levels.VipMaintainLpCost);
        }

        [Fact]
        public void Overlay_AppliesASavedLadder_AndTheEngineUsesIt()
        {
            var tiers = GoodTiers();
            tiers[2] = new VipConfig.TierRow { SpThreshold = 10_000, SpendFloorUsd = 0m, BonusPct = 0.05m, Factor = 2m };   // Silver
            var levels = GoodLevels();
            levels[0] = new VipConfig.LevelRow { Threshold = 1_000, BonusPct = 0.5m, MaintainLp = 50 };                     // VIP 1

            var cfg = VipConfig.Overlay(Base(), new Dictionary<string, string>
            {
                ["Vip:Tiers"] = VipConfig.SerializeTiers(tiers),
                ["Vip:Levels"] = VipConfig.SerializeLevels(levels),
            });

            Assert.Equal(10_000L, cfg.SpThresholds[2]);
            Assert.Equal(2m, VipMath.TierFactor(Khela.Common.Leaderboards.VipTier.Silver, cfg));
            Assert.Equal(1_000L, VipMath.VipLevelThreshold(1, cfg));
            Assert.Equal(50L, VipMath.MaintainLpCost(1, cfg));
            // …and the level-up loop honours the new, cheaper rung: 2,500 progress at Silver's ×2 clears VIP 1 (1,000).
            var (progress, level) = VipMath.ApplyVipLevelUps(0, 0, 2_500, cfg);
            Assert.Equal(1, level);
            Assert.Equal(1_500L, progress);
        }

        // ---- fail whole ----

        [Fact]
        public void ATierLadderThatIsWrong_FallsBackWhole()
        {
            var b = Base();
            void Same((long[] Sp, decimal[] Floors, decimal[] Bonus, decimal[] Factors) got)
                => Assert.Equal(b.SpThresholds, got.Sp);

            Same(VipConfig.ParseTiers("", b));
            Same(VipConfig.ParseTiers("{ not json", b));

            var short7 = GoodTiers().Take(7).ToList();                       // the tier enum is 8 wide
            Same(VipConfig.ParseTiers(VipConfig.SerializeTiers(short7), b));

            var descending = GoodTiers();
            descending[5] = new VipConfig.TierRow { SpThreshold = 1, SpendFloorUsd = 0m, BonusPct = 0.1m, Factor = 2m };
            Same(VipConfig.ParseTiers(VipConfig.SerializeTiers(descending), b));   // a ladder must not go down

            var negative = GoodTiers();
            negative[3] = new VipConfig.TierRow { SpThreshold = 100, SpendFloorUsd = -1m, BonusPct = 0m, Factor = 1m };
            Same(VipConfig.ParseTiers(VipConfig.SerializeTiers(negative), b));
        }

        [Fact]
        public void ALevelLadderThatIsWrong_FallsBackWhole()
        {
            var b = Base();
            Assert.Equal(b.VipLevelThresholds, VipConfig.ParseLevels("", b).VipLevelThresholds);
            Assert.Equal(b.VipLevelThresholds, VipConfig.ParseLevels("[]", b).VipLevelThresholds);

            // A zero threshold would let a level be reached for nothing — and ApplyVipLevelUps would spin on it.
            var zero = GoodLevels();
            zero[4] = new VipConfig.LevelRow { Threshold = 0, BonusPct = 1m, MaintainLp = 10 };
            Assert.Equal(b.VipLevelThresholds, VipConfig.ParseLevels(VipConfig.SerializeLevels(zero), b).VipLevelThresholds);
        }

        [Fact]
        public void ATierLadderThatLetsTierNoneGrind_IsRefused()
        {
            // The ONE invariant the whole ladder rests on: below the VIP entry level a player grinds nothing. The web form
            // refuses it, but a seed file or a direct Redis edit reaches the same key — so the parser must refuse it too.
            var b = Base();
            var rows = GoodTiers();
            rows[0] = new VipConfig.TierRow { SpThreshold = 0, SpendFloorUsd = 0m, BonusPct = 0m, Factor = 1m };
            Assert.Equal(b.TierFactors, VipConfig.ParseTiers(VipConfig.SerializeTiers(rows), b).TierFactors);
        }

        [Fact]
        public void TheLevelCap_FollowsTheLADDER_NotALiteralTen()
        {
            // A ladder is whatever the admin saved. Three rungs: a player must stop at 3, and the top must read as the top.
            var three = GoodLevels().Take(3).ToList();
            var cfg = VipConfig.Overlay(Base(), new Dictionary<string, string> { ["Vip:Levels"] = VipConfig.SerializeLevels(three) });
            Assert.Equal(4, cfg.VipLevelThresholds.Length);            // [0] unused + 3 levels
            Assert.Equal(3, VipMath.TopVipLevel(cfg));
            Assert.Equal(7_500_000L, VipMath.VipLevelThreshold(3, cfg));
            Assert.Equal(long.MaxValue, VipMath.VipLevelThreshold(4, cfg));   // out of range = unreachable

            // Enough progress for ten levels stops at the ladder's top and banks the rest, rather than spinning on a
            // threshold that isn't there.
            var (progress, level) = VipMath.ApplyVipLevelUps(0, 0, 500_000_000, cfg);
            Assert.Equal(3, level);
            Assert.Equal(500_000_000 - (5_000_000 + 6_000_000 + 7_500_000), progress);

            // A LONGER ladder is climbable past 10 — the old literal cap would have stranded every rung above it.
            var twelve = GoodLevels().ToList();
            twelve.Add(new VipConfig.LevelRow { Threshold = 30_000_000, BonusPct = 1.8m, MaintainLp = 15_000 });
            twelve.Add(new VipConfig.LevelRow { Threshold = 33_000_000, BonusPct = 1.9m, MaintainLp = 17_000 });
            var big = VipConfig.Overlay(Base(), new Dictionary<string, string> { ["Vip:Levels"] = VipConfig.SerializeLevels(twelve) });
            Assert.Equal(12, VipMath.TopVipLevel(big));
            Assert.Equal(12, VipMath.ApplyVipLevelUps(0, 11, 33_000_000, big).Level);
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
