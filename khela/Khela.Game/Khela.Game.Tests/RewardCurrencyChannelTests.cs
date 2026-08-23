using Khela.Game.Database.Models;
using Khela.Game.Services.Exchange;
using Khela.Game.Services.Rewards;
using Khela.Game.Services.Wallet;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// The three currency CHANNELS (docs/VIP_SPEC.md §1). One shared allowlist used to gate rewards, the store and the
    /// exchange alike; the three progression points need different rules, and getting them wrong is not cosmetic — a
    /// chest that can mint VIP-P, or an exchange into it, makes VIP level (and the cashback it pays) farmable by play.
    /// These are the guardrails, pinned.
    /// </summary>
    public class RewardCurrencyChannelTests
    {
        // ---- the enum is APPEND-ONLY (persisted as int on PlayerWallets.Currency + every ledger row) ----

        [Fact]
        public void CurrencyValues_AreFrozen()
        {
            Assert.Equal(0, (int)CurrencyType.Chips);
            Assert.Equal(1, (int)CurrencyType.Coins);
            Assert.Equal(2, (int)CurrencyType.Gems);
            Assert.Equal(3, (int)CurrencyType.Tokens);
            Assert.Equal(4, (int)CurrencyType.Kash);
            Assert.Equal(5, (int)CurrencyType.Sp);
            Assert.Equal(6, (int)CurrencyType.Lp);
            Assert.Equal(7, (int)CurrencyType.VipPoints);
        }

        // ---- the legal guardrail: only play money is wagerable, and the token is nowhere ----

        [Theory]
        [InlineData(CurrencyType.Chips, true)]
        [InlineData(CurrencyType.Coins, true)]
        [InlineData(CurrencyType.Gems, false)]
        [InlineData(CurrencyType.Kash, false)]
        [InlineData(CurrencyType.Tokens, false)]
        [InlineData(CurrencyType.Sp, false)]
        [InlineData(CurrencyType.Lp, false)]
        [InlineData(CurrencyType.VipPoints, false)]
        public void OnlyChipsAndCoins_AreWagerable(CurrencyType c, bool wagerable)
            => Assert.Equal(wagerable, WalletService.IsWagerableCurrency(c));

        [Fact]
        public void TheToken_IsOnNoChannelAtAll()
        {
            Assert.False(RewardCurrencies.IsGrantable(CurrencyType.Tokens));
            Assert.False(RewardCurrencies.IsSellable(CurrencyType.Tokens));
            Assert.False(RewardCurrencies.IsExchangeableFrom(CurrencyType.Tokens));
            Assert.False(RewardCurrencies.IsExchangeableTo(CurrencyType.Tokens));
            Assert.True(RewardCurrencies.IsForbidden(CurrencyType.Tokens));
        }

        // ---- VIP-P: the record of MONEY ----

        [Fact]
        public void VipPoints_AreSoldOnly_NeverGrantedByPlay_NeverExchanged()
        {
            Assert.True(RewardCurrencies.IsSellable(CurrencyType.VipPoints));       // a store product may pay it
            Assert.False(RewardCurrencies.IsGrantable(CurrencyType.VipPoints));     // a chest / pass / mission / daily may not
            Assert.False(RewardCurrencies.IsExchangeableFrom(CurrencyType.VipPoints));
            Assert.False(RewardCurrencies.IsExchangeableTo(CurrencyType.VipPoints));
        }

        // ---- SP: status, not value ----

        [Fact]
        public void Sp_IsGrantableAndSellable_ButNeverExchanged()
        {
            Assert.True(RewardCurrencies.IsGrantable(CurrencyType.Sp));
            Assert.True(RewardCurrencies.IsSellable(CurrencyType.Sp));
            Assert.False(RewardCurrencies.IsExchangeableFrom(CurrencyType.Sp));   // can't sell your badge on
            Assert.False(RewardCurrencies.IsExchangeableTo(CurrencyType.Sp));     // can't buy it with chips
        }

        // ---- LP: a comp you may spend, never buy ----

        [Fact]
        public void Lp_ExchangesOneWayOnly()
        {
            Assert.True(RewardCurrencies.IsGrantable(CurrencyType.Lp));
            Assert.True(RewardCurrencies.IsSellable(CurrencyType.Lp));
            Assert.True(RewardCurrencies.IsExchangeableFrom(CurrencyType.Lp));    // LP → chips
            Assert.False(RewardCurrencies.IsExchangeableTo(CurrencyType.Lp));     // chips → LP would be buying comp
        }

        // ---- and the exchange validator enforces exactly that ----

        private static ExchangeCatalogConfig Pair(string from, string to) => new ExchangeCatalogConfig
        {
            Pairs = { new ExchangePairDef { Key = "p", FromCurrency = from, ToCurrency = to, FromPerUnit = 100m, Step = 1m, MinTo = 1m, Enabled = true } },
        };

        [Fact]
        public void TheExchangeValidator_AcceptsLpToChips_AndRefusesEveryPointBridge()
        {
            Assert.Null(ExchangeCatalog.Validate(Pair("Lp", "Chips")));

            Assert.Contains("can never be exchanged FOR", ExchangeCatalog.Validate(Pair("Chips", "Lp")));
            Assert.Contains("can never be exchanged FROM", ExchangeCatalog.Validate(Pair("VipPoints", "Chips")));
            Assert.Contains("can never be exchanged FOR", ExchangeCatalog.Validate(Pair("Chips", "VipPoints")));
            Assert.Contains("can never be exchanged FROM", ExchangeCatalog.Validate(Pair("Sp", "Chips")));
            Assert.Contains("can never be exchanged FOR", ExchangeCatalog.Validate(Pair("Chips", "Sp")));
            Assert.Contains("can never be exchanged FROM", ExchangeCatalog.Validate(Pair("Tokens", "Chips")));
            Assert.Contains("can never be exchanged FOR", ExchangeCatalog.Validate(Pair("Chips", "Tokens")));
        }

        // ---- parse helpers gate per channel ----

        [Fact]
        public void ParseHelpers_ApplyTheirOwnChannel()
        {
            Assert.True(RewardCurrencies.TryParseSellable("VipPoints", out var sold));
            Assert.Equal(CurrencyType.VipPoints, sold);
            Assert.False(RewardCurrencies.TryParseGrantable("VipPoints", out _));

            Assert.True(RewardCurrencies.TryParseGrantable("lp", out var lp));      // case-insensitive
            Assert.Equal(CurrencyType.Lp, lp);

            Assert.False(RewardCurrencies.TryParseGrantable("7", out _));           // numeric forms stay rejected
            Assert.False(RewardCurrencies.TryParseSellable("7", out _));
            Assert.False(RewardCurrencies.TryParseGrantable("Tokens", out _));
            Assert.False(RewardCurrencies.TryParseSellable("Tokens", out _));
        }
    }
}
