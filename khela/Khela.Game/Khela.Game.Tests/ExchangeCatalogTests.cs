using System;
using System.Collections.Generic;
using System.Linq;
using Khela.Game.Services.Exchange;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// The exchange catalog is pure (no I/O), so its guardrails are pinned here: the seed is E-03 (1,000,000 chips = 1 Kash,
    /// one way), Tokens can never be on either side, the arithmetic is exact, a round trip must lose value, and the per-player
    /// refusals fire in the right order.
    /// </summary>
    public class ExchangeCatalogTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        private static ExchangePairDef Pair(string key, string from, string to, decimal fromPerUnit, decimal step = 1m, decimal min = 1m)
            => new ExchangePairDef { Key = key, FromCurrency = from, ToCurrency = to, FromPerUnit = fromPerUnit, Step = step, MinTo = min, Enabled = true };

        private static ExchangeCatalogConfig Cfg(params ExchangePairDef[] pairs) => new ExchangeCatalogConfig { Pairs = pairs.ToList() };

        // ---- the seed ----

        [Fact]
        public void Defaults_AreValid_AndAreE03()
        {
            var cfg = ExchangeCatalog.Defaults();
            Assert.Null(ExchangeCatalog.Validate(cfg));
            var p = cfg.Find("chips_kash");
            Assert.Equal("Chips", p.FromCurrency); Assert.Equal("Kash", p.ToCurrency);
            Assert.Equal(1_000_000m, p.FromPerUnit);
            Assert.Null(cfg.Find("kash_chips"));                                   // one way
            Assert.Equal(5_000_000m, ExchangeCatalog.Cost(p, 5m));                  // 5 Kash = 5M chips, exactly
        }

        // ---- arithmetic ----

        [Theory]
        [InlineData(1, 1_000_000)]
        [InlineData(7, 7_000_000)]
        [InlineData(100, 100_000_000)]
        public void Cost_IsExact_NoRounding(decimal toAmount, decimal expected)
            => Assert.Equal(expected, ExchangeCatalog.Cost(Pair("x", "Chips", "Kash", 1_000_000m), toAmount));

        [Fact]
        public void StepAlignment()
        {
            var p = Pair("x", "Chips", "Kash", 1_000_000m, step: 5m, min: 5m);
            Assert.True(ExchangeCatalog.AlignsToStep(p, 5m));
            Assert.True(ExchangeCatalog.AlignsToStep(p, 25m));
            Assert.False(ExchangeCatalog.AlignsToStep(p, 7m));
            Assert.False(ExchangeCatalog.AlignsToStep(p, 0m));
            Assert.False(ExchangeCatalog.AlignsToStep(p, -5m));
            var frac = Pair("y", "Chips", "Gems", 1_000m, step: 0.5m, min: 0.5m);
            Assert.True(ExchangeCatalog.AlignsToStep(frac, 1.5m));
            Assert.False(ExchangeCatalog.AlignsToStep(frac, 1.25m));
        }

        // ---- validator ----

        [Fact]
        public void Tokens_CanNeverBeOnEitherSide()
        {
            Assert.Contains("can never be exchanged", ExchangeCatalog.Validate(Cfg(Pair("a", "Chips", "Tokens", 10m))));
            Assert.Contains("can never be exchanged", ExchangeCatalog.Validate(Cfg(Pair("b", "Tokens", "Chips", 10m))));
            Assert.Contains("not a currency name", ExchangeCatalog.Validate(Cfg(Pair("c", "Chips", "Bitcoin", 10m))));
        }

        [Fact]
        public void SameCurrency_BadRate_BadStep_BadLimits_AreRejected()
        {
            Assert.Contains("same currency", ExchangeCatalog.Validate(Cfg(Pair("a", "Chips", "chips", 10m))));
            Assert.Contains("rate", ExchangeCatalog.Validate(Cfg(Pair("a", "Chips", "Kash", 0m))));
            Assert.Contains("step", ExchangeCatalog.Validate(Cfg(Pair("a", "Chips", "Kash", 10m, step: 0m))));
            Assert.Contains("multiple of the step", ExchangeCatalog.Validate(Cfg(Pair("a", "Chips", "Kash", 10m, step: 5m, min: 3m))));

            var p = Pair("a", "Chips", "Kash", 10m, step: 1m, min: 10m); p.MaxToPerTx = 5m;
            Assert.Contains("below the minimum", ExchangeCatalog.Validate(Cfg(p)));
            p = Pair("a", "Chips", "Kash", 10m, min: 10m); p.DailyCapTo = 5m;
            Assert.Contains("daily cap is below", ExchangeCatalog.Validate(Cfg(p)));
            p = Pair("a", "Chips", "Kash", 10m); p.MinLevel = -1;
            Assert.Contains("negative", ExchangeCatalog.Validate(Cfg(p)));
            p = Pair("a", "Chips", "Kash", 10m); p.FromUtc = T0.AddDays(1); p.ToUtc = T0;
            Assert.Contains("ends before it starts", ExchangeCatalog.Validate(Cfg(p)));
        }

        [Fact]
        public void Keys_AreUnique_AndSafe_AndOneEnabledPairPerRoute()
        {
            Assert.Contains("duplicate", ExchangeCatalog.Validate(Cfg(Pair("a", "Chips", "Kash", 10m), Pair("a", "Chips", "Gems", 10m))));
            Assert.Contains("a-z", ExchangeCatalog.Validate(Cfg(Pair("Bad Key!", "Chips", "Kash", 10m))));
            Assert.Contains("already offered by", ExchangeCatalog.Validate(Cfg(Pair("a", "Chips", "Kash", 10m), Pair("b", "chips", "KASH", 20m))));
            // A DISABLED duplicate route is fine — authoring ahead / a future rate.
            var b = Pair("b", "Chips", "Kash", 20m); b.Enabled = false;
            Assert.Null(ExchangeCatalog.Validate(Cfg(Pair("a", "Chips", "Kash", 10m), b)));
        }

        [Fact]
        public void RoundTrip_MustLoseValue()
        {
            // Chips → Kash at 1,000,000 per Kash; Kash → Chips at 0.000002 Kash per chip (1 Kash = 500,000 chips), step 50,000
            // chips so the cost quantum is 0.1 Kash: F_ab × F_ba = 1,000,000 × 0.000002 = 2 > 1 → lossy → allowed.
            var ab = Pair("chips_kash", "Chips", "Kash", 1_000_000m);
            var baLossy = Pair("kash_chips", "Kash", "Chips", 0.000002m, step: 50_000m, min: 50_000m);
            Assert.Null(ExchangeCatalog.Validate(Cfg(ab, baLossy)));
            Assert.Single(ExchangeCatalog.Cycles(Cfg(ab, baLossy)));
            Assert.Equal(0.5m, ExchangeCatalog.Cycles(Cfg(ab, baLossy))[0].ReturnFactor);

            // 1 Kash buys 1,000,000 chips back: F_ab × F_ba = 1 → break-even → refused.
            var baEven = Pair("kash_chips", "Kash", "Chips", 0.000001m, step: 100_000m, min: 100_000m);
            Assert.Contains("round trip must lose value", ExchangeCatalog.Validate(Cfg(ab, baEven)));

            // 1 Kash buys 1,250,000 chips back (0.0000008 per chip) → the round trip prints 25% → refused.
            var baProfit = Pair("kash_chips", "Kash", "Chips", 0.0000008m, step: 125_000m, min: 125_000m);
            Assert.Contains("round trip must lose value", ExchangeCatalog.Validate(Cfg(ab, baProfit)));

            Assert.True(ExchangeCatalog.RoundTripNotLossy(2m, 0.5m));
            Assert.True(ExchangeCatalog.RoundTripNotLossy(2m, 0.4m));
            Assert.False(ExchangeCatalog.RoundTripNotLossy(2m, 0.6m));
        }

        [Fact]
        public void ThreeWayLoops_MustLoseToo()
        {
            // No pair has a reverse, yet Chips → Kash → Gems → Chips doubles chips per lap: 1M chips → 1 Kash → 200 Gems →
            // 2M chips. Refused. Shrink the last leg (1 Gem = 4,000 chips) and the lap returns 0.8× → allowed, and reported.
            var chipsKash = Pair("chips_kash", "Chips", "Kash", 1_000_000m);
            var kashGems = Pair("kash_gems", "Kash", "Gems", 0.005m, step: 20m, min: 20m);           // 1 Kash = 200 Gems; quantum 0.1 Kash
            var gemsChipsBad = Pair("gems_chips", "Gems", "Chips", 0.0001m, step: 10_000m, min: 10_000m);   // 1 Gem = 10,000 chips; quantum 1 Gem
            var err = ExchangeCatalog.Validate(Cfg(chipsKash, kashGems, gemsChipsBad));
            Assert.NotNull(err);
            Assert.Contains("every round trip must lose value", err);
            Assert.Contains("chips_kash", err);

            var gemsChipsOk = Pair("gems_chips", "Gems", "Chips", 0.00025m, step: 4_000m, min: 4_000m);   // 1 Gem = 4,000 chips; quantum 1 Gem
            var cfg = Cfg(chipsKash, kashGems, gemsChipsOk);
            Assert.Null(ExchangeCatalog.Validate(cfg));
            var cycles = ExchangeCatalog.Cycles(cfg);
            Assert.Single(cycles);
            Assert.Equal(3, cycles[0].Pairs.Count);
            Assert.Equal(0.8m, cycles[0].ReturnFactor);
            Assert.Contains("0.8×", ExchangeCatalog.RoundTripNotes(cfg)[0]);
        }

        [Fact]
        public void RatesFinerThanTheWallet_AreRefused_UnlessTheStepMakesTheCostExact()
        {
            // 0.000002 Kash per chip with step 1 → a 24-chip exchange would cost 0.000048 Kash, which decimal(18,4) rounds to
            // 0.0000: chips for nothing. Refused. The same rate with step 50 (quantum 0.0001 Kash) is exact → allowed.
            var ab = Pair("chips_kash", "Chips", "Kash", 1_000_000m);
            var fine = Pair("kash_chips", "Kash", "Chips", 0.000002m, step: 1m, min: 1m);
            Assert.Contains("below the wallet's precision", ExchangeCatalog.Validate(Cfg(ab, fine)));
            var ok = Pair("kash_chips", "Kash", "Chips", 0.000002m, step: 50m, min: 50m);
            Assert.Null(ExchangeCatalog.Validate(Cfg(ab, ok)));
            Assert.Equal(0.0001m, ExchangeCatalog.Cost(ok, 50m));
            Assert.Equal(0.0012m, ExchangeCatalog.Cost(ok, 600m));

            // A quantum that is not a whole number of 0.0001 (step 3 × 0.00005 = 0.00015) is refused too.
            var odd = Pair("kash_chips", "Kash", "Chips", 0.00005m, step: 3m, min: 3m);
            Assert.Contains("not a whole number of 0.0001", ExchangeCatalog.Validate(Cfg(ab, odd)));

            // Steps / caps finer than the wallet can hold are refused.
            Assert.Contains("step must be a whole number", ExchangeCatalog.Validate(Cfg(Pair("x", "Chips", "Gems", 1_000m, step: 0.00005m, min: 0.00005m))));
            var capFine = Pair("y", "Chips", "Kash", 1_000_000m); capFine.DailyCapTo = 0.00005m;
            Assert.Contains("whole numbers of 0.0001", ExchangeCatalog.Validate(Cfg(capFine)));
        }

        [Fact]
        public void Refusal_RefusesACostTheWalletCannotHoldExactly()
        {
            // Belt-and-braces at runtime, independent of the validator: a pair object whose cost is sub-quantum is refused.
            var fine = Pair("kash_chips", "Kash", "Chips", 0.000002m, step: 1m, min: 1m);
            Assert.Equal("This amount can't be exchanged at this rate.", ExchangeCatalog.Refusal(fine, 24m, 0, 0, 10, T0));
            var ok = Pair("kash_chips", "Kash", "Chips", 0.000002m, step: 50m, min: 50m);
            Assert.Null(ExchangeCatalog.Refusal(ok, 50m, 0, 0, 10, T0));
        }

        [Fact]
        public void Key_IsCanonicalisedOnValidate()
        {
            var cfg = Cfg(Pair(" chips_kash ", "Chips", "Kash", 1_000_000m));
            Assert.Null(ExchangeCatalog.Validate(cfg));
            Assert.Equal("chips_kash", cfg.Pairs[0].Key);
            Assert.NotNull(cfg.Find("chips_kash"));
        }

        // ---- per-player refusals ----

        [Fact]
        public void Refusal_FiresInOrder_AndNullWhenAllowed()
        {
            var p = Pair("chips_kash", "Chips", "Kash", 1_000_000m, step: 1m, min: 2m);
            p.MaxToPerTx = 50m; p.DailyCapTo = 100m; p.LifetimeCapTo = 1_000m; p.MinLevel = 5; p.FromUtc = T0; p.ToUtc = T0.AddDays(7);

            Assert.Equal("Not available yet.", ExchangeCatalog.Refusal(p, 10m, 0, 0, 10, T0.AddMinutes(-1)));
            Assert.Equal("This exchange has ended.", ExchangeCatalog.Refusal(p, 10m, 0, 0, 10, T0.AddDays(7)));
            Assert.Equal("Unlocks at level 5.", ExchangeCatalog.Refusal(p, 10m, 0, 0, 4, T0));
            Assert.Equal("Choose an amount.", ExchangeCatalog.Refusal(p, 0m, 0, 0, 10, T0));
            Assert.Contains("multiple of 1", ExchangeCatalog.Refusal(p, 1.5m, 0, 0, 10, T0));
            Assert.Contains("Minimum is 2", ExchangeCatalog.Refusal(p, 1m, 0, 0, 10, T0));
            Assert.Contains("Maximum per exchange is 50", ExchangeCatalog.Refusal(p, 51m, 0, 0, 10, T0));
            Assert.Contains("left today", ExchangeCatalog.Refusal(p, 20m, usedToday: 90m, usedLifetime: 90m, 10, T0));
            Assert.Equal("Daily limit reached — come back tomorrow.", ExchangeCatalog.Refusal(p, 2m, usedToday: 100m, usedLifetime: 100m, 10, T0));
            Assert.Contains("left.", ExchangeCatalog.Refusal(p, 20m, usedToday: 0m, usedLifetime: 990m, 10, T0));
            Assert.Null(ExchangeCatalog.Refusal(p, 10m, 0, 0, 10, T0));

            var disabled = Pair("d", "Chips", "Kash", 10m); disabled.Enabled = false;
            Assert.Equal("This exchange is not available.", ExchangeCatalog.Refusal(disabled, 1m, 0, 0, 10, T0));
        }

        // ---- persistence ----

        [Fact]
        public void Json_RoundTrips_AndCanonicalisesCurrencyNames()
        {
            var cfg = Cfg(Pair("chips_kash", "chips", "KASH", 1_000_000m));
            cfg.Pairs[0].DailyCapTo = 25m; cfg.Pairs[0].FromUtc = T0;
            Assert.Null(ExchangeCatalog.Validate(cfg));                           // validate canonicalises
            Assert.Equal("Chips", cfg.Pairs[0].FromCurrency); Assert.Equal("Kash", cfg.Pairs[0].ToCurrency);

            var back = ExchangeCatalog.TryParse(ExchangeCatalog.ToJson(cfg));
            Assert.NotNull(back);
            Assert.Null(ExchangeCatalog.Validate(back));
            Assert.Equal(ExchangeCatalog.ToJson(cfg), ExchangeCatalog.ToJson(back));
            Assert.Equal(25m, back.Find("CHIPS_KASH").DailyCapTo);
            Assert.Equal(T0, back.Pairs[0].FromUtc);

            Assert.Null(ExchangeCatalog.TryParse(""));
            Assert.Null(ExchangeCatalog.TryParse("{ not json"));
            Assert.NotNull(ExchangeCatalog.TryParse("{\"version\":1,\"pairs\":[]}"));   // empty = nothing offered
        }
    }
}
