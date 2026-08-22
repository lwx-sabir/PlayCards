using System;
using System.Collections.Generic;
using System.Linq;
using Khela.Common.Rewards;
using Khela.Common.Store;
using Khela.Game.Services.Store;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// The store catalog is pure (no I/O), so its guardrails are pinned here: the seed ladder obeys the agreed economics
    /// (docs/IAP_SPEC.md §3.1), Tokens can never be sold, random payloads are refused by default, effects must have a
    /// handler, ids/store ids are unique, and the JSON round-trips exactly.
    /// </summary>
    public class StoreCatalogTests
    {
        private static StoreCatalogConfig Seed() => StoreCatalog.Defaults();

        private static StoreProductDef Chips(string id, decimal chips, decimal usd) => new StoreProductDef
        {
            Id = id, Section = "chips", StoreIds = StoreCatalog.BothStores(id), UsdReference = usd,
            Lines = { RewardGrant.Currency("Chips", chips) },
        };

        private static StoreCatalogConfig One(StoreProductDef p) => new StoreCatalogConfig { Products = { p } };

        [Fact]
        public void Defaults_AreValid()
        {
            Assert.Null(StoreCatalog.Validate(Seed()));
        }

        [Fact]
        public void Defaults_ChipLadder_ValuePerDollarRises_AndNeverBeatsThePiggy()
        {
            // §3.1: minimums 5M/$1.99 and 10M/$2.99; value/$ rises up the ladder but stays ≤ the piggy's ~5M/$.
            var cfg = Seed();
            var chips = cfg.Products.Where(p => p.Id.StartsWith("chips_")).OrderBy(p => p.SortOrder).ToList();
            Assert.Equal(7, chips.Count);
            Assert.Equal(5_000_000m, chips[0].Lines[0].Amount); Assert.Equal(1.99m, chips[0].UsdReference);
            Assert.Equal(10_000_000m, chips[1].Lines[0].Amount); Assert.Equal(2.99m, chips[1].UsdReference);

            decimal prev = 0m;
            foreach (var p in chips)
            {
                var perUsd = p.ChipsPerUsd();
                Assert.True(perUsd >= prev, $"{p.Id} value/$ fell below the previous rung");
                prev = perUsd;
            }
            // piggy tier 1 = 10M for $1.99 → 5.025M/$; the whale anchor (500M/$99.99 ≈ 5.0005M/$) stays under it.
            Assert.True(StoreCatalog.BestChipsPerUsd(cfg) <= 10_000_000m / 1.99m);
        }

        [Fact]
        public void Defaults_HaveTheAgreedEntryPoints()
        {
            var cfg = Seed();
            var kash = cfg.Find("kash_01");
            Assert.Equal(100m, kash.Lines[0].Amount); Assert.Equal(1.99m, kash.UsdReference);
            Assert.Equal("Kash", kash.Lines[0].Id);

            var piggy = cfg.Find("piggy_t1_full");
            Assert.Equal(StoreCatalog.EffectPiggyBreak, piggy.Effect.Type);
            Assert.Equal("Full", piggy.Effect.Arg);
            Assert.Equal(1, StoreCatalog.PiggyTierOf(piggy));
            Assert.Equal(1.99m, piggy.UsdReference);
            Assert.Equal(3.99m, cfg.Find("piggy_t1_x2").UsdReference);

            Assert.Equal(StoreProductType.Subscription, cfg.Find("golden_pass").ProductType);
            Assert.False(cfg.Find("starter_pack").Enabled);   // a template until the offer is decided
        }

        [Fact]
        public void Tokens_CanNeverBeSold()
        {
            var p = Chips("bad_tokens", 1m, 0.99m);
            p.Lines = new List<RewardGrant> { RewardGrant.Currency("Tokens", 1m) };
            Assert.Contains("may never be sold", StoreCatalog.Validate(One(p)));

            // numeric currency ids are refused too — "3" must never resolve to Tokens by accident
            p.Lines = new List<RewardGrant> { RewardGrant.Currency("3", 1m) };
            Assert.Contains("not a currency name", StoreCatalog.Validate(One(p)));
        }

        [Fact]
        public void RandomPayloads_RefusedUnlessAllowed()
        {
            var p = Chips("pack_chest", 1m, 0.99m);
            p.Lines = new List<RewardGrant> { RewardGrant.Chest("CK_Chest:Rare") };
            Assert.Contains("random", StoreCatalog.Validate(One(p)), StringComparison.OrdinalIgnoreCase);
            Assert.Null(StoreCatalog.Validate(One(p), allowRandomPayloads: true));
        }

        [Fact]
        public void DuplicateIds_AndDuplicateStoreIds_AreRejected()
        {
            var cfg = new StoreCatalogConfig { Products = { Chips("a", 1m, 0.99m), Chips("a", 2m, 1.99m) } };
            Assert.Contains("duplicate product id", StoreCatalog.Validate(cfg));

            var b = Chips("b", 1m, 0.99m);
            b.StoreIds[StorePlatform.GooglePlay.ToString()] = "a";   // same Play id as product a
            cfg = new StoreCatalogConfig { Products = { Chips("a", 1m, 0.99m), b } };
            Assert.Contains("used by another product", StoreCatalog.Validate(cfg));
        }

        [Fact]
        public void Ids_MustBeStoreSafe()
        {
            Assert.Contains("a-z", StoreCatalog.Validate(One(Chips("Bad Id!", 1m, 0.99m))));
            var p = Chips("ok_id.1", 1m, 0.99m);
            p.StoreIds[StorePlatform.AppStore.ToString()] = "Not Ok";
            Assert.Contains("a-z", StoreCatalog.Validate(One(p)));
        }

        [Fact]
        public void UnknownEffect_Or_BadEffectArgs_AreRejected()
        {
            var p = new StoreProductDef { Id = "fx", StoreIds = StoreCatalog.BothStores("fx"), UsdReference = 0.99m, Effect = new StoreEffectDef { Type = "Teleport" } };
            Assert.Contains("no grant handler", StoreCatalog.Validate(One(p)));

            p.Effect = new StoreEffectDef { Type = StoreCatalog.EffectPiggyBreak, Arg = "Sideways" };
            Assert.Contains("Full | FullDouble | Early", StoreCatalog.Validate(One(p)));

            p.Effect = new StoreEffectDef { Type = StoreCatalog.EffectPiggyBreak, Arg = "Full" };   // no tier
            Assert.Contains("tier", StoreCatalog.Validate(One(p)));

            p.Effect = new StoreEffectDef { Type = StoreCatalog.EffectVipBooster, Arg = "Forever" };
            Assert.Contains("Time | LevelUp", StoreCatalog.Validate(One(p)));

            // a handler set narrower than the built-ins wins (the registry decides what is fulfillable)
            p.Effect = new StoreEffectDef { Type = StoreCatalog.EffectVipBooster, Arg = "Time" };
            Assert.Null(StoreCatalog.Validate(One(p)));
            Assert.Contains("no grant handler", StoreCatalog.Validate(One(p), new HashSet<string> { StoreCatalog.EffectPiggyBreak }));
        }

        [Fact]
        public void Subscription_NeedsAnEffect_AndGoldenPassMustBeASubscription()
        {
            var sub = Chips("sub", 1m, 4.99m);
            sub.ProductType = StoreProductType.Subscription;
            Assert.Contains("subscription must carry an effect", StoreCatalog.Validate(One(sub)));

            var golden = new StoreProductDef { Id = "golden", StoreIds = StoreCatalog.BothStores("golden"), UsdReference = 4.99m, Effect = new StoreEffectDef { Type = StoreCatalog.EffectGoldenPass, Arg = "monthly" } };
            Assert.Contains("must be a Subscription", StoreCatalog.Validate(One(golden)));
            golden.ProductType = StoreProductType.Subscription;
            Assert.Null(StoreCatalog.Validate(One(golden)));
        }

        [Fact]
        public void ProductThatGrantsNothing_IsRejected()
        {
            var p = new StoreProductDef { Id = "nothing", StoreIds = StoreCatalog.BothStores("nothing"), UsdReference = 0.99m };
            Assert.Contains("grants nothing", StoreCatalog.Validate(One(p)));
        }

        [Fact]
        public void StoreIdFor_ResolvesPerPlatform_AndFakeSellsEverything()
        {
            var p = Chips("x", 1m, 0.99m);
            p.StoreIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["GooglePlay"] = "x.play" };
            Assert.Equal("x.play", StoreCatalog.StoreIdFor(p, StorePlatform.GooglePlay));
            Assert.Null(StoreCatalog.StoreIdFor(p, StorePlatform.AppStore));
            Assert.Null(StoreCatalog.StoreIdFor(p, StorePlatform.Unknown));
            Assert.Equal("x", StoreCatalog.StoreIdFor(p, StorePlatform.Fake));

            var cfg = One(p);
            Assert.Same(p, StoreCatalog.ResolveByStoreId(cfg, StorePlatform.GooglePlay, "X.PLAY"));
            Assert.Null(StoreCatalog.ResolveByStoreId(cfg, StorePlatform.AppStore, "x.play"));
            Assert.Equal(new[] { "x: no store id for AppStore" }, StoreCatalog.MissingStoreIds(cfg, new[] { StorePlatform.GooglePlay, StorePlatform.AppStore, StorePlatform.Fake }));
        }

        [Fact]
        public void Json_RoundTrips_Exactly()
        {
            var cfg = Seed();
            var json = StoreCatalog.ToJson(cfg);
            var back = StoreCatalog.TryParse(json);
            Assert.NotNull(back);
            Assert.Null(StoreCatalog.Validate(back));
            Assert.Equal(cfg.Products.Count, back.Products.Count);
            Assert.Equal(StoreCatalog.ToJson(cfg), StoreCatalog.ToJson(back));
            // case-insensitive keys survive the trip
            Assert.Equal("chips_01", back.Find("chips_01").StoreIds["googleplay"]);
            Assert.Equal(1, StoreCatalog.PiggyTierOf(back.Find("piggy_t1_early")));
        }

        [Fact]
        public void TryParse_RejectsGarbage_AndAllowsAnEmptyCatalog()
        {
            Assert.Null(StoreCatalog.TryParse(""));
            Assert.Null(StoreCatalog.TryParse("{not json"));
            var empty = StoreCatalog.TryParse("{\"version\":1,\"products\":[]}");
            Assert.NotNull(empty);
            Assert.Null(StoreCatalog.Validate(empty));                         // nothing on sale is legal
        }

        [Fact]
        public void ValueGuard_FlagsAPiggyOfferWorseThanTheBestPack()
        {
            var cfg = Seed();
            // tier-1 Full: 10M for $1.99 = 5.03M/$ ≥ best pack (5.0005M/$) → fine; a 5M bank at the same price is worse → warned
            Assert.Empty(StoreCatalog.PiggyValueWarnings(cfg, new[] { ("piggy_t1_full", 10_000_000m) }));
            var warnings = StoreCatalog.PiggyValueWarnings(cfg, new[] { ("piggy_t1_full", 5_000_000m) });
            Assert.Single(warnings);
            Assert.StartsWith("piggy_t1_full", warnings[0]);
        }

        [Fact]
        public void Availability_RulesAreEnforcedInOrder()
        {
            var p = Chips("lim", 1m, 0.99m);
            p.Availability = new StoreAvailabilityDef { MaxPerUser = 1, MaxPerUserPerDay = 1, MinLevel = 5, FromUtc = new DateTime(2026, 1, 1), ToUtc = new DateTime(2027, 1, 1) };
            var now = new DateTime(2026, 6, 1);
            Assert.Null(StoreCatalogService.Ineligible(p, 0, 0, 5, now));
            Assert.Equal("Unlocks at level 5.", StoreCatalogService.Ineligible(p, 0, 0, 4, now));
            Assert.Equal("Already purchased.", StoreCatalogService.Ineligible(p, 1, 0, 5, now));
            Assert.Equal("Not available yet.", StoreCatalogService.Ineligible(p, 0, 0, 5, new DateTime(2025, 12, 31)));
            Assert.Equal("This offer has ended.", StoreCatalogService.Ineligible(p, 0, 0, 5, new DateTime(2027, 1, 1)));
            p.Availability.MaxPerUser = 0;
            Assert.Equal("Daily limit reached — come back tomorrow.", StoreCatalogService.Ineligible(p, 3, 1, 5, now));
        }
    }
}
