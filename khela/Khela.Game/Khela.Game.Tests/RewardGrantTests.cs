using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Khela.Game.Database.Models;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Rewards;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the reward payout seam (docs/PASS_SPEC.md §2): the currency ALLOWLIST that keeps the tradeable token
    /// un-grantable (CLAUDE.md NON-NEGOTIABLE #2/#4), the id parsing, the wallet-key clamp that protects the 64-char
    /// uniquely-indexed CorrelationId, and the dispatch rules (per-line idempotency keys, unknown kinds skipped,
    /// expansion). Pure — no DB, no Redis, no wallet.
    /// </summary>
    public class RewardGrantTests
    {
        // ---- allowlist (the legal guardrail) ----

        [Theory]
        [InlineData(CurrencyType.Chips)]
        [InlineData(CurrencyType.Coins)]
        [InlineData(CurrencyType.Gems)]
        [InlineData(CurrencyType.Kash)]
        public void Allowlist_PermitsPlayAndSpendCurrencies(CurrencyType c) => Assert.True(RewardCurrencies.IsGrantable(c));

        [Fact]
        public void Allowlist_NeverPermitsTheTradeableToken()
        {
            Assert.False(RewardCurrencies.IsGrantable(CurrencyType.Tokens));
            Assert.True(RewardCurrencies.IsForbidden(CurrencyType.Tokens));
            Assert.False(RewardCurrencies.TryParseGrantable("Tokens", out _));
        }

        [Fact]
        public void Allowlist_RejectsAnyUndefinedOrFutureCurrencyValue()
            => Assert.False(RewardCurrencies.IsGrantable((CurrencyType)99));

        [Fact]
        public void ChestCatalog_SharesTheSameAllowlist()
        {
            // One allowlist for every reward system — chests must not be able to drift away from it.
            Assert.False(ChestCatalog.IsAllowedReward(CurrencyType.Tokens));
            Assert.True(ChestCatalog.IsAllowedReward(CurrencyType.Kash));
        }

        // ---- currency id parsing ----

        [Theory]
        [InlineData("Chips", CurrencyType.Chips)]
        [InlineData("kash", CurrencyType.Kash)]
        [InlineData("  Gems ", CurrencyType.Gems)]
        public void TryParse_AcceptsNamesCaseInsensitively(string id, CurrencyType expected)
        {
            Assert.True(RewardCurrencies.TryParse(id, out var c));
            Assert.Equal(expected, c);
        }

        [Theory]
        [InlineData("3")]        // the token's int — a numeric config value must never resolve to a currency
        [InlineData("0")]
        [InlineData("Chip5")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Nonsense")]
        public void TryParse_RejectsNumericAndUnknownForms(string id) => Assert.False(RewardCurrencies.TryParse(id, out _));

        // ---- wallet key clamp (CorrelationId is MaxLength(64), uniquely indexed per wallet) ----

        [Fact]
        public void WalletKey_LeavesShortKeysExactlyAsTheyAre()
        {
            const string key = "reward:8f14e45fceea167a5a36dedd4bea2543";
            Assert.Equal(key, RewardIds.WalletKey(key));   // historical keys must stay byte-identical (idempotency)
        }

        [Fact]
        public void WalletKey_SqueezesLongKeysDeterministicallyAndDistinctly()
        {
            var a = new string('a', 80) + ":free:0";
            var b = new string('a', 80) + ":free:1";

            Assert.Equal(64, RewardIds.WalletKey(a).Length);
            Assert.Equal(RewardIds.WalletKey(a), RewardIds.WalletKey(a));   // stable across calls → still idempotent
            Assert.NotEqual(RewardIds.WalletKey(a), RewardIds.WalletKey(b)); // differs where the source differs
        }

        // ---- chest id parsing ----

        [Fact]
        public void TryParseChest_SplitsKeyAndTier()
        {
            Assert.True(RewardIds.TryParseChest("CK_Chest:Rare", out var key, out var tier));
            Assert.Equal("CK_Chest", key);
            Assert.Equal(ChestTier.Rare, tier);
        }

        [Theory]
        [InlineData("CK_Chest")]
        [InlineData("CK_Chest:")]
        [InlineData(":Rare")]
        [InlineData("CK_Chest:Legendary")]   // not a defined tier
        [InlineData(null)]
        public void TryParseChest_RejectsMalformedIds(string id) => Assert.False(RewardIds.TryParseChest(id, out _, out _));

        // ---- dispatch ----

        private sealed class FakeGranter : IRewardGranter
        {
            private readonly int _expand;
            public FakeGranter(RewardKind kind, int expand = 1) { Kind = kind; _expand = expand; }
            public RewardKind Kind { get; }
            public List<string> Keys { get; } = new List<string>();

            public Task<IReadOnlyList<GrantedLineDto>> GrantAsync(Guid userId, RewardGrant line, string idemKey, string description, string externalRef = null)
            {
                Keys.Add(idemKey);
                var lines = new List<GrantedLineDto>();
                for (int i = 0; i < _expand; i++) lines.Add(new GrantedLineDto { Kind = (int)Kind, Id = line.Id, Amount = line.Amount });
                return Task.FromResult<IReadOnlyList<GrantedLineDto>>(lines);
            }
        }

        private static RewardGrantService Service(params IRewardGranter[] granters)
            => new RewardGrantService(granters, NullLogger<RewardGrantService>.Instance);

        [Fact]
        public async Task GrantAll_UsesAPositionalKeyPerLine()
        {
            var currency = new FakeGranter(RewardKind.Currency);
            var xp = new FakeGranter(RewardKind.Xp);
            var svc = Service(currency, xp);

            var applied = await svc.GrantAllAsync(Guid.NewGuid(), new[]
            {
                RewardGrant.Currency("Chips", 1000m),
                RewardGrant.Xp(50),
            }, "pass:2026-09:u:7:free");

            // Keys are a function of POSITION only, so a retry of the same payload lands on the same keys.
            Assert.Equal(new[] { "pass:2026-09:u:7:free:0" }, currency.Keys);
            Assert.Equal(new[] { "pass:2026-09:u:7:free:1" }, xp.Keys);
            Assert.Equal(2, applied.Count);
        }

        [Fact]
        public async Task GrantOne_UsesTheKeyVerbatim()
        {
            var currency = new FakeGranter(RewardKind.Currency);
            await Service(currency).GrantOneAsync(Guid.NewGuid(), RewardGrant.Currency("Chips", 10m), "reward:abc");
            Assert.Equal(new[] { "reward:abc" }, currency.Keys);   // the inbox's historical key shape, unchanged
        }

        [Fact]
        public async Task UnknownKind_IsSkippedWithoutBlockingTheRestOfThePayload()
        {
            var currency = new FakeGranter(RewardKind.Currency);
            var svc = Service(currency);   // no Cosmetic granter registered on this build

            var applied = await svc.GrantAllAsync(Guid.NewGuid(), new[]
            {
                new RewardGrant { Kind = RewardKind.Cosmetic, Id = "sku_hat", Amount = 1m },
                RewardGrant.Currency("Chips", 500m),
            }, "pass:x");

            Assert.Single(applied);
            Assert.Equal((int)RewardKind.Currency, applied[0].Kind);
            Assert.Equal(new[] { "pass:x:1" }, currency.Keys);   // position is preserved even when a line is skipped
        }

        [Fact]
        public async Task ALineMayExpandIntoSeveralAppliedLines()
        {
            var chest = new FakeGranter(RewardKind.Chest, expand: 3);   // chest → its rolled contents
            var applied = await Service(chest).GrantAllAsync(Guid.NewGuid(), new[] { RewardGrant.Chest("CK_Chest:Rare") }, "k");
            Assert.Equal(3, applied.Count);
        }

        [Fact]
        public async Task NoLines_OrNoKey_GrantsNothing()
        {
            var currency = new FakeGranter(RewardKind.Currency);
            var svc = Service(currency);
            Assert.Empty(await svc.GrantAllAsync(Guid.NewGuid(), Array.Empty<RewardGrant>(), "k"));
            Assert.Empty(await svc.GrantAllAsync(Guid.NewGuid(), new[] { RewardGrant.Currency("Chips", 1m) }, null));
            Assert.Empty(currency.Keys);
        }

        [Fact]
        public void CanGrant_ReportsWhatThisBuildCanPayOut()
        {
            var svc = Service(new FakeGranter(RewardKind.Currency), new FakeGranter(RewardKind.Xp));
            Assert.True(svc.CanGrant(RewardKind.Currency));
            Assert.False(svc.CanGrant(RewardKind.Item));   // admin-save validation uses this to refuse un-payable rewards
        }
    }
}
