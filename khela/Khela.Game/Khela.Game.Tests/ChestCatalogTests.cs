using System;
using System.Collections.Generic;
using Khela.Game.Database.Models;
using Khela.Game.Services.Chests;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the chest catalog's admin-save validation — above all the legal guardrail that a chest may NEVER grant the
    /// tradeable token (CLAUDE.md NON-NEGOTIABLE #2/#4: players never WIN the token by playing; it's revenue-backed). Also
    /// covers range sanity (Min ≤ Max, non-negative), non-empty keys, rewards present, and duplicate (Key, Tier). Pure —
    /// no DB, no Redis. The open-time skip of a forbidden currency is defense-in-depth, exercised by the live smoke.
    /// </summary>
    public class ChestCatalogTests
    {
        private static ChestRewardRange R(CurrencyType c, long min, long max) => new ChestRewardRange { Currency = c, Min = min, Max = max };

        private static ChestConfig One(params ChestRewardRange[] rewards) => new ChestConfig
        {
            Chests = new List<ChestDef>
            {
                new ChestDef { Key = "CK_Chest", Tier = ChestTier.Common, Title = "T", Rewards = new List<ChestRewardRange>(rewards) },
            },
        };

        [Fact]
        public void Defaults_AreValid_AndContainNoForbiddenCurrency()
        {
            var cfg = ChestCatalog.Defaults();
            Assert.Null(ChestCatalog.Validate(cfg));
            foreach (var c in cfg.Chests)
                foreach (var r in c.Rewards)
                    Assert.False(ChestCatalog.IsForbidden(r.Currency), $"{c.Key}/{c.Tier} has forbidden {r.Currency}");
        }

        [Theory]
        [InlineData(CurrencyType.Chips)]
        [InlineData(CurrencyType.Coins)]
        [InlineData(CurrencyType.Gems)]
        [InlineData(CurrencyType.Kash)]
        public void Validate_AllowsEveryNonTokenCurrency(CurrencyType currency)
            => Assert.Null(ChestCatalog.Validate(One(R(currency, 1, 10))));

        [Fact]
        public void Validate_RejectsTokenReward()
        {
            var err = ChestCatalog.Validate(One(R(CurrencyType.Chips, 1, 10), R(CurrencyType.Tokens, 1, 5)));
            Assert.NotNull(err);
            Assert.Contains("token", err, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void IsForbidden_OnlyTokens()
        {
            Assert.True(ChestCatalog.IsForbidden(CurrencyType.Tokens));
            Assert.False(ChestCatalog.IsForbidden(CurrencyType.Chips));
            Assert.False(ChestCatalog.IsForbidden(CurrencyType.Coins));
            Assert.False(ChestCatalog.IsForbidden(CurrencyType.Gems));
            Assert.False(ChestCatalog.IsForbidden(CurrencyType.Kash));
        }

        [Fact]
        public void Validate_RejectsMaxBelowMin()
            => Assert.NotNull(ChestCatalog.Validate(One(R(CurrencyType.Chips, 100, 10))));

        [Fact]
        public void Validate_RejectsNegativeAmounts()
            => Assert.NotNull(ChestCatalog.Validate(One(R(CurrencyType.Chips, -1, 10))));

        [Fact]
        public void Validate_RejectsEmptyKey()
        {
            var cfg = One(R(CurrencyType.Chips, 1, 10));
            cfg.Chests[0].Key = "";
            Assert.NotNull(ChestCatalog.Validate(cfg));
        }

        [Fact]
        public void Validate_RejectsRewardlessChest()
            => Assert.NotNull(ChestCatalog.Validate(One()));

        [Fact]
        public void Validate_RejectsDuplicateKeyTier_CaseInsensitive()
        {
            var cfg = new ChestConfig
            {
                Chests = new List<ChestDef>
                {
                    new ChestDef { Key = "CK_Chest", Tier = ChestTier.Common, Rewards = new List<ChestRewardRange> { R(CurrencyType.Chips, 1, 10) } },
                    new ChestDef { Key = "ck_chest", Tier = ChestTier.Common, Rewards = new List<ChestRewardRange> { R(CurrencyType.Chips, 1, 10) } },
                },
            };
            Assert.NotNull(ChestCatalog.Validate(cfg));
        }

        [Fact]
        public void Validate_RejectsEmptyConfig()
        {
            Assert.NotNull(ChestCatalog.Validate(new ChestConfig()));
            Assert.NotNull(ChestCatalog.Validate(null));
        }

        [Fact]
        public void Find_IsCaseInsensitiveOnKey_AndTierExact()
        {
            var cfg = ChestCatalog.Defaults();
            Assert.NotNull(cfg.Find("ck_chest", ChestTier.Common));
            Assert.NotNull(cfg.Find("CK_Chest", ChestTier.Rare));
            Assert.Null(cfg.Find("nope", ChestTier.Common));
        }

        [Fact]
        public void Validate_RejectsUndefinedCurrencyInt()
        {
            // System.Text.Json deserializes an out-of-range int into an undefined enum value with no error; the
            // allowlist must still reject it (fail-closed), not just the known Tokens value.
            Assert.NotNull(ChestCatalog.Validate(One(R((CurrencyType)99, 1, 10))));
        }

        // The open-time guardrail: RollRewards is what OpenAsync credits from, so this locks that a token / undefined /
        // non-permitted currency can NEVER be credited even by a bad override that bypassed admin-save validation
        // (e.g. a raw Redis write) — independent of the live smoke. Pure, so no wallet/Redis fakes needed.
        [Fact]
        public void RollRewards_DropsTokenAndUndefinedCurrencies_KeepsAllowed()
        {
            var def = new ChestDef
            {
                Key = "K", Tier = ChestTier.Common, Title = "T",
                Rewards = new List<ChestRewardRange>
                {
                    R(CurrencyType.Chips, 100, 100),
                    R(CurrencyType.Tokens, 50, 50),     // forbidden token
                    R((CurrencyType)99, 7, 7),          // undefined currency
                    R(CurrencyType.Kash, 5, 5),
                },
            };
            var rolled = ChestCatalog.RollRewards(def, "open-1");
            Assert.DoesNotContain(rolled, x => x.Currency == CurrencyType.Tokens);
            Assert.DoesNotContain(rolled, x => (int)x.Currency == 99);
            Assert.Contains(rolled, x => x.Currency == CurrencyType.Chips && x.Amount == 100);
            Assert.Contains(rolled, x => x.Currency == CurrencyType.Kash && x.Amount == 5);
            Assert.Equal(2, rolled.Count);
        }

        [Fact]
        public void RollRewards_IsDeterministicPerOpenKey_AndInRange()
        {
            var def = One(R(CurrencyType.Chips, 1, 1_000_000)).Chests[0];
            var a = ChestCatalog.RollRewards(def, "key-1");
            var b = ChestCatalog.RollRewards(def, "key-1");
            Assert.Equal(a[0].Amount, b[0].Amount);       // same open key → identical roll (retry-safe)
            Assert.InRange(a[0].Amount, 1L, 1_000_000L);
            Assert.InRange(ChestCatalog.RollRewards(def, "key-2")[0].Amount, 1L, 1_000_000L);
        }
    }
}
