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
    /// Time-limited sales (docs/IAP_SPEC.md §3.4). Two things have to hold: a value bonus pays exactly what the card showed
    /// — same formula both sides, decided from the purchase's snapshot at its reserve time, not from whatever the catalog
    /// says when the receipt arrives — and a price-off SKU can never quietly be a different product. Pure, no I/O.
    /// </summary>
    public class StoreSaleTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        private static StoreProductDef Chips(string id, decimal chips, decimal usd) => new StoreProductDef
        {
            Id = id, Section = "chips", StoreIds = StoreCatalog.BothStores(id), UsdReference = usd,
            Lines = { RewardGrant.Currency("Chips", chips) },
        };

        /// <summary>A PriceOff sale SKU: the cheaper twin that DECLARES which regular it belongs to (its own gate).</summary>
        private static StoreProductDef Sku(string id, decimal chips, decimal usd, string of)
        {
            var p = Chips(id, chips, usd);
            p.SaleSkuOf = of;
            return p;
        }

        private static StoreSaleDef Bonus(int percent, DateTime? from = null, DateTime? to = null)
            => new StoreSaleDef { Kind = StoreSaleKind.ValueBonus, Percent = percent, FromUtc = from, ToUtc = to ?? T0.AddDays(2) };

        private static StoreSaleDef Off(int percent, string sku, DateTime? to = null)
            => new StoreSaleDef { Kind = StoreSaleKind.PriceOff, Percent = percent, SaleProductId = sku, ToUtc = to ?? T0.AddDays(2) };

        private static StoreCatalogConfig Cfg(params StoreProductDef[] products) => new StoreCatalogConfig { Products = products.ToList() };

        // ---- the formula ----

        [Theory]
        [InlineData(1_000, 33, 1_330)]
        [InlineData(5_000_000, 50, 7_500_000)]
        [InlineData(3, 50, 4)]          // 4.5 → 4: floored, never rounded up
        [InlineData(100, 0, 100)]       // no bonus, untouched
        [InlineData(0, 50, 0)]
        public void Boost_FloorsToWholeUnits(decimal amount, int percent, decimal expected)
            => Assert.Equal(expected, StoreSaleMath.Boost(amount, percent));

        [Fact]
        public void Apply_BoostsCurrencyAndXp_LeavesOtherKindsAlone_AndDoesNotTouchTheInput()
        {
            var lines = new List<RewardGrant> { RewardGrant.Currency("Chips", 1_000m), RewardGrant.Xp(100), RewardGrant.Chest("CK_Chest:Rare") };
            var boosted = StoreSaleMath.Apply(lines, 50);

            Assert.Equal(1_500m, boosted[0].Amount);
            Assert.Equal(150m, boosted[1].Amount);
            Assert.Equal(1m, boosted[2].Amount);               // a chest is a chest
            Assert.Equal(1_000m, lines[0].Amount);             // input untouched
            Assert.NotSame(lines, boosted);
        }

        // ---- the window ----

        [Fact]
        public void SaleActive_RespectsTheWindow_AndGraceOnlyExtendsTheEnd()
        {
            var sale = Bonus(50, from: T0, to: T0.AddHours(1));

            Assert.False(StoreCatalog.SaleActive(sale, T0.AddMinutes(-1)));
            Assert.True(StoreCatalog.SaleActive(sale, T0));
            Assert.True(StoreCatalog.SaleActive(sale, T0.AddMinutes(59)));
            Assert.False(StoreCatalog.SaleActive(sale, T0.AddHours(1)));                       // the end is exclusive

            // Grace: for GRANTING a receipt that lands after the sheet, never for showing.
            Assert.True(StoreCatalog.SaleActive(sale, T0.AddHours(1).AddMinutes(10), grace: true));
            Assert.False(StoreCatalog.SaleActive(sale, T0.AddHours(1) + StoreCatalog.SaleGrace, grace: true));
            Assert.False(StoreCatalog.SaleActive(sale, T0.AddMinutes(-1), grace: true));      // grace never opens the start early
        }

        [Fact]
        public void SaleActive_IsFalseForNoneOrZeroPercent()
        {
            Assert.False(StoreCatalog.SaleActive(null, T0));
            Assert.False(StoreCatalog.SaleActive(new StoreSaleDef { Kind = StoreSaleKind.None, Percent = 50, ToUtc = T0.AddDays(1) }, T0));
            Assert.False(StoreCatalog.SaleActive(new StoreSaleDef { Kind = StoreSaleKind.ValueBonus, Percent = 0, ToUtc = T0.AddDays(1) }, T0));
        }

        [Fact]
        public void TheGrantDecision_IsTheSnapshotAtReserveTime()
        {
            // What GrantAsync does: ActiveSale(snapshot, row.CreatedAt, grace: true). The snapshot is the product as it
            // was when the purchase was reserved — the catalog may have changed since; that must not matter.
            var snapshot = Chips("chips_02", 10_000_000m, 2.99m);
            snapshot.Sale = Bonus(100, from: T0, to: T0.AddHours(6));

            Assert.NotNull(StoreCatalog.ActiveSale(snapshot, T0.AddHours(3), grace: true));                // reserved inside
            Assert.NotNull(StoreCatalog.ActiveSale(snapshot, T0.AddHours(6).AddMinutes(10), grace: true)); // sheet ran past the end
            Assert.Null(StoreCatalog.ActiveSale(snapshot, T0.AddHours(6).AddMinutes(20), grace: true));    // too late — no promise was made
            Assert.Null(StoreCatalog.ActiveSale(snapshot, T0.AddMinutes(-5), grace: true));                 // reserved before it began

            // And the amount it would pay is the formula's, on the snapshot's lines.
            Assert.Equal(20_000_000m, StoreSaleMath.Apply(snapshot.Lines, snapshot.Sale.Percent)[0].Amount);
        }

        // ---- validator: value bonus ----

        [Fact]
        public void ValueBonus_Valid()
        {
            var p = Chips("chips_02", 10_000_000m, 2.99m);
            p.Sale = Bonus(50, to: T0.AddDays(1));
            Assert.Null(StoreCatalog.Validate(Cfg(p)));
        }

        [Fact]
        public void ValueBonus_NeedsAnEnd_AValidPercent_AndSomethingToBoost()
        {
            var p = Chips("chips_02", 10_000_000m, 2.99m);

            p.Sale = new StoreSaleDef { Kind = StoreSaleKind.ValueBonus, Percent = 50 };                 // no end
            Assert.Contains("needs an end", StoreCatalog.Validate(Cfg(p)));

            p.Sale = Bonus(0);
            Assert.Contains("1–1000", StoreCatalog.Validate(Cfg(p)));
            p.Sale = Bonus(StoreCatalog.MaxValueBonusPercent + 1);
            Assert.Contains("1–1000", StoreCatalog.Validate(Cfg(p)));

            p.Sale = Bonus(50, from: T0.AddDays(3), to: T0.AddDays(2));                                   // ends before it starts
            Assert.Contains("ends before it starts", StoreCatalog.Validate(Cfg(p)));

            // An effect-only product has nothing to boost.
            var piggy = new StoreProductDef
            {
                Id = "piggy_t1_full", StoreIds = StoreCatalog.BothStores("piggy_t1_full"), UsdReference = 1.99m,
                Effect = new StoreEffectDef { Type = StoreCatalog.EffectPiggyBreak, Arg = "Full", Params = new Dictionary<string, string> { ["tier"] = "1" } },
                Sale = Bonus(50),
            };
            Assert.Contains("currency or XP line", StoreCatalog.Validate(Cfg(piggy)));
        }

        [Fact]
        public void Subscriptions_CannotCarryASale()
        {
            var golden = new StoreProductDef
            {
                Id = "golden_pass", ProductType = StoreProductType.Subscription, StoreIds = StoreCatalog.BothStores("golden_pass"), UsdReference = 4.99m,
                Lines = { RewardGrant.Currency("Chips", 1m) },
                Effect = new StoreEffectDef { Type = StoreCatalog.EffectGoldenPass, Arg = "monthly" },
                Sale = Bonus(50),
            };
            Assert.Contains("subscription can't carry a sale", StoreCatalog.Validate(Cfg(golden)));
        }

        // ---- validator: price off ----

        [Fact]
        public void PriceOff_Valid_WhenTheSkuIsACheaperTwin()
        {
            var regular = Chips("chips_03", 18_000_000m, 4.99m);
            var sku = Sku("chips_03_sale", 18_000_000m, 2.99m, "chips_03");
            regular.Sale = Off(40, "chips_03_sale");
            Assert.Null(StoreCatalog.Validate(Cfg(regular, sku)));
            Assert.Same(regular, StoreCatalog.RegularFor(Cfg(regular, sku), "chips_03_sale"));
            Assert.Null(StoreCatalog.RegularFor(Cfg(regular, sku), "chips_03"));

            // A plain product can't be pressed into service as a discount: the SKU must declare itself.
            var undeclared = Chips("chips_03_sale", 18_000_000m, 2.99m);
            Assert.Contains("must declare", StoreCatalog.Validate(Cfg(regular, undeclared)));
        }

        [Fact]
        public void ADeclaredSku_IsGated_ByItsOwnDeclaration_NotTheRegularsPointer()
        {
            // The SKU's declaration is the gate. With a live PriceOff pointing at it: a gate with that sale. After "Clear"
            // (the pointer gone) the SKU is still recognised — and unsellable, not free. This is what makes Clear safe.
            var regular = Chips("chips_03", 18_000_000m, 4.99m);
            var sku = Sku("chips_03_sale", 18_000_000m, 2.99m, "chips_03");
            regular.Sale = Off(40, "chips_03_sale");
            var cfg = Cfg(regular, sku);

            var gate = StoreCatalog.GateFor(cfg, sku);
            Assert.NotNull(gate); Assert.Same(regular, gate.Regular); Assert.Same(regular.Sale, gate.Sale);
            Assert.Null(StoreCatalog.GateFor(cfg, regular));                       // an ordinary product has no gate

            regular.Sale = null;                                                     // Clear
            Assert.Null(StoreCatalog.Validate(cfg));                                 // legal: the SKU is simply unsellable now
            var closed = StoreCatalog.GateFor(cfg, sku);
            Assert.NotNull(closed); Assert.Null(closed.Sale);
            Assert.Equal("This offer has ended.", StoreCatalogService.Ineligible(sku, 0, 0, 99, T0, closed));

            // Re-targeting the regular at another SKU while this one still declares it: refused, not silently orphaned.
            regular.Sale = Off(40, "chips_03_other");
            var other = Sku("chips_03_other", 18_000_000m, 2.99m, "chips_03");
            Assert.Contains("points at", StoreCatalog.Validate(Cfg(regular, sku, other)));

            // A declaration naming a product that doesn't exist, or itself.
            var orphan = Sku("lonely", 1m, 0.99m, "ghost");
            Assert.Contains("not in the catalog", StoreCatalog.Validate(Cfg(orphan)));
            var selfish = Sku("selfish", 1m, 0.99m, "selfish");
            Assert.Contains("own regular", StoreCatalog.Validate(Cfg(selfish)));
        }

        [Fact]
        public void PriceOff_RefusesAMissingSelfOrDifferentSku()
        {
            var regular = Chips("chips_03", 18_000_000m, 4.99m);

            regular.Sale = Off(40, "");
            Assert.Contains("needs the sale SKU", StoreCatalog.Validate(Cfg(regular)));

            regular.Sale = Off(40, "nope");
            Assert.Contains("not in the catalog", StoreCatalog.Validate(Cfg(regular)));

            regular.Sale = Off(40, "chips_03");
            Assert.Contains("own sale SKU", StoreCatalog.Validate(Cfg(regular)));

            // A different payload wearing a discount.
            regular.Sale = Off(40, "chips_03_sale");
            var smaller = Chips("chips_03_sale", 17_000_000m, 2.99m);
            Assert.Contains("must pay exactly", StoreCatalog.Validate(Cfg(regular, smaller)));

            var otherType = Chips("chips_03_sale", 18_000_000m, 2.99m); otherType.ProductType = StoreProductType.NonConsumable;
            Assert.Contains("different product type", StoreCatalog.Validate(Cfg(regular, otherType)));
        }

        [Fact]
        public void PriceOff_SkuMustBeCheaper_AndTheRegularMustBePriced()
        {
            var regular = Chips("chips_03", 18_000_000m, 4.99m);
            regular.Sale = Off(40, "chips_03_sale");

            Assert.Contains("LOWER reference price", StoreCatalog.Validate(Cfg(regular, Chips("chips_03_sale", 18_000_000m, 4.99m))));
            Assert.Contains("LOWER reference price", StoreCatalog.Validate(Cfg(regular, Chips("chips_03_sale", 18_000_000m, 5.99m))));
            Assert.Contains("LOWER reference price", StoreCatalog.Validate(Cfg(regular, Chips("chips_03_sale", 18_000_000m, 0m))));

            var unpriced = Chips("chips_03", 18_000_000m, 0m);
            unpriced.Sale = Off(40, "chips_03_sale");
            Assert.Contains("price the product", StoreCatalog.Validate(Cfg(unpriced, Chips("chips_03_sale", 18_000_000m, 2.99m))));
        }

        [Fact]
        public void PriceOff_NoChains_AndOneSkuServesOneSale()
        {
            var regular = Chips("chips_03", 18_000_000m, 4.99m);
            regular.Sale = Off(40, "chips_03_sale");
            var sku = Sku("chips_03_sale", 18_000_000m, 2.99m, "chips_03");
            sku.Sale = Bonus(10);
            Assert.Contains("can't carry a sale of its own", StoreCatalog.Validate(Cfg(regular, sku)));

            // A second regular pointing at a SKU that declares the first: the SKU's declaration wins and the save is refused.
            sku.Sale = null;
            var another = Chips("chips_03b", 18_000_000m, 5.99m);
            another.Sale = Off(50, "chips_03_sale");
            Assert.Contains("must declare 'sale SKU of chips_03b'", StoreCatalog.Validate(Cfg(regular, sku, another)));
        }

        [Fact]
        public void SamePayload_IgnoresLineOrder_ComparesEffectsWithParams()
        {
            var a = new StoreProductDef { Lines = { RewardGrant.Currency("Chips", 5m), RewardGrant.Currency("Kash", 1m) } };
            var b = new StoreProductDef { Lines = { RewardGrant.Currency("Kash", 1m), RewardGrant.Currency("chips", 5.0m) } };
            Assert.True(StoreCatalog.SamePayload(a, b));

            var e1 = new StoreProductDef { Effect = new StoreEffectDef { Type = "PiggyBreak", Arg = "Full", Params = new Dictionary<string, string> { ["tier"] = "1" } } };
            var e2 = new StoreProductDef { Effect = new StoreEffectDef { Type = "PiggyBreak", Arg = "Full", Params = new Dictionary<string, string> { ["tier"] = "2" } } };
            var e3 = new StoreProductDef { Effect = new StoreEffectDef { Type = "piggybreak", Arg = "full", Params = new Dictionary<string, string> { ["TIER"] = "1" } } };
            Assert.False(StoreCatalog.SamePayload(e1, e2));
            Assert.True(StoreCatalog.SamePayload(e1, e3));
        }

        // ---- persistence ----

        [Fact]
        public void Json_RoundTrips_TheSale()
        {
            var regular = Chips("chips_03", 18_000_000m, 4.99m);
            regular.Sale = Off(40, "chips_03_sale", to: T0.AddDays(1));
            regular.Sale.Label = "WEEKEND";
            var sku = Sku("chips_03_sale", 18_000_000m, 2.99m, "chips_03");
            var boosted = Chips("chips_02", 10_000_000m, 2.99m);
            boosted.Sale = Bonus(50, from: T0, to: T0.AddDays(1));

            var back = StoreCatalog.TryParse(StoreCatalog.ToJson(Cfg(regular, sku, boosted)));
            Assert.NotNull(back);
            Assert.Null(StoreCatalog.Validate(back));
            Assert.Equal(StoreSaleKind.PriceOff, back.Find("chips_03").Sale.Kind);
            Assert.Equal("chips_03_sale", back.Find("chips_03").Sale.SaleProductId);
            Assert.Equal("WEEKEND", back.Find("chips_03").Sale.Label);
            Assert.Equal(StoreSaleKind.ValueBonus, back.Find("chips_02").Sale.Kind);
            Assert.Equal(T0, back.Find("chips_02").Sale.FromUtc);
            Assert.Null(back.Find("chips_03_sale").Sale);
            Assert.Equal("chips_03", back.Find("chips_03_sale").SaleSkuOf);
        }

        // ---- eligibility of a sale SKU ----

        [Fact]
        public void ASaleSku_IsBuyableOnlyInsideItsSalesWindow()
        {
            var regular = Chips("chips_03", 18_000_000m, 4.99m);
            var sku = Sku("chips_03_sale", 18_000_000m, 2.99m, "chips_03");     // no availability of its own
            var window = Off(40, "chips_03_sale", to: T0.AddHours(2));
            window.FromUtc = T0;
            var gate = new SaleSkuGate { Regular = regular, Sale = window };

            Assert.Equal("Not available yet.", StoreCatalogService.Ineligible(sku, 0, 0, 1, T0.AddMinutes(-1), gate));
            Assert.Null(StoreCatalogService.Ineligible(sku, 0, 0, 1, T0.AddHours(1), gate));
            Assert.Equal("This offer has ended.", StoreCatalogService.Ineligible(sku, 0, 0, 1, T0.AddHours(2), gate));
            Assert.Null(StoreCatalogService.Ineligible(sku, 0, 0, 1, T0.AddDays(9), null));   // not a sale SKU: its own (empty) rules
        }

        [Fact]
        public void ASaleSku_InheritsTheRegularsLimits_WithTheCountsMerged()
        {
            // starter_pack is "max 1 per player, level 5+". Its $1.99 twin has no rules of its own. The pair is ONE offer:
            // the SKU must obey the regular's rules, judged on regular + SKU purchases together — otherwise "one per player"
            // is one at full price plus N at the discount.
            var regular = Chips("starter_pack", 25_000_000m, 2.99m);
            regular.Availability = new StoreAvailabilityDef { MaxPerUser = 1, MinLevel = 5 };
            var sku = Sku("starter_pack_sale", 25_000_000m, 1.99m, "starter_pack");
            var gate = new SaleSkuGate { Regular = regular, Sale = Off(33, "starter_pack_sale", to: T0.AddDays(1)) };

            Assert.Equal("Unlocks at level 5.", StoreCatalogService.Ineligible(sku, 0, 0, 3, T0, gate));
            Assert.Null(StoreCatalogService.Ineligible(sku, 0, 0, 5, T0, gate));
            Assert.Equal("Already purchased.", StoreCatalogService.Ineligible(sku, 1, 0, 5, T0, gate));   // 1 = bought the regular OR the SKU before
        }

        [Fact]
        public void TheSaleIsJudgedAtTheStoresPurchaseTime_WhenKnown()
        {
            // The store's purchase time is the truth: a cash payment completed hours after the sale, an app killed before the
            // redeem, a restore — all reserve late but were BOUGHT when the card showed the bonus. And one bought before a
            // scheduled sale and redeemed inside it was not. Never later than the reserve (a store clock ahead of ours).
            var reserved = T0.AddHours(5);
            Assert.Equal(T0.AddHours(1), StoreCatalog.SaleDecisionTime(reserved, T0.AddHours(1)));   // bought earlier → judged then
            Assert.Equal(reserved, StoreCatalog.SaleDecisionTime(reserved, null));                   // no store time → the reserve
            Assert.Equal(reserved, StoreCatalog.SaleDecisionTime(reserved, reserved.AddMinutes(3)));  // store clock ahead → ours

            var snapshot = Chips("chips_02", 10_000_000m, 2.99m);
            snapshot.Sale = Bonus(50, from: T0, to: T0.AddHours(2));
            // Paid at T0+1h (inside), receipt at T0+5h (long after the grace): still the bonus.
            Assert.NotNull(StoreCatalog.ActiveSale(snapshot, StoreCatalog.SaleDecisionTime(T0.AddHours(5), T0.AddHours(1)), grace: true));
            // Paid at T0-1h (before a scheduled sale), receipt at T0+30min (inside): no bonus — the card never showed it.
            Assert.Null(StoreCatalog.ActiveSale(snapshot, StoreCatalog.SaleDecisionTime(T0.AddMinutes(30), T0.AddHours(-1)), grace: true));
        }
    }
}
