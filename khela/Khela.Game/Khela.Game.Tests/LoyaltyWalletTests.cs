using System;
using System.Linq;
using Khela.Game.Database.Models;
using Khela.Game.Services.Exchange;
using Khela.Game.Services.Loyalty;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// LP moved onto the wallet (docs/VIP_SPEC.md §3). Two things had to be right and are pinned here: the correlation ids
    /// that make every LP movement idempotent must FIT the ledger's 64-char budget whatever the source id looks like (a
    /// round id plus a user id is already over it), and the LP → chips exchange pair must ship in a state that cannot pay
    /// anyone until its rate is set deliberately — the rate is what decides whether LP is a ~1% comp or a 100% loop.
    /// </summary>
    public class LoyaltyWalletTests
    {
        // ---- correlation ids ----

        [Fact]
        public void Key_StaysInsideTheLedgersBudget_ForAnySourceId()
        {
            var user = Guid.NewGuid();

            // A round id is itself a Guid: "lpw:" + 36 + ":" + 32 is already 73 — this MUST hash rather than throw later
            // inside the wallet (the ledger's CorrelationId is varchar(64)).
            var round = Guid.NewGuid().ToString();
            var key = LoyaltyService.Key("lpw", round, user);
            Assert.True(key.Length <= 64, $"length {key.Length}: {key}");
            Assert.StartsWith("lpw:", key);

            // Deterministic — a retry of the same accrual keys the same movement, which is what makes it idempotent.
            Assert.Equal(key, LoyaltyService.Key("lpw", round, user));

            // Different round, different user, different prefix → all different.
            Assert.NotEqual(key, LoyaltyService.Key("lpw", Guid.NewGuid().ToString(), user));
            Assert.NotEqual(key, LoyaltyService.Key("lpw", round, Guid.NewGuid()));
            Assert.NotEqual(key, LoyaltyService.Key("lpp", round, user));

            // A short source id stays readable rather than being hashed for no reason.
            var shortKey = LoyaltyService.Key("lpr", "abc", user);
            Assert.Contains(":abc:", shortKey);
            Assert.True(shortKey.Length <= 64);

            // And an absurd source id is still inside the budget.
            Assert.True(LoyaltyService.Key("lpr", new string('x', 400), user).Length <= 64);
        }

        // ---- the LP → chips pair ----

        [Fact]
        public void TheLpPair_ShipsDisabled_AtAnHonestOnePercent()
        {
            var cfg = ExchangeCatalog.Defaults();
            var pair = cfg.Find("lp_chips");
            Assert.NotNull(pair);
            Assert.False(pair.Enabled);                       // the rate is a decision — it cannot pay before it is made
            Assert.Equal("Lp", pair.FromCurrency);
            Assert.Equal("Chips", pair.ToCurrency);

            // 1 LP per chip against Loyalty:LpChipsPerPoint = 100 chips wagered per LP is a 1% comp:
            // chips ÷ (LP × chipsPerLp) = 1000 ÷ (1000 × 100) = 1%.
            var lpPerChip = pair.FromPerUnit;
            var chipsPerLpEarned = new LoyaltyConfig().LpChipsPerPoint;
            var costLp = ExchangeCatalog.Cost(pair, 1_000m);
            Assert.Equal(1_000m, costLp);
            Assert.Equal(0.01m, 1_000m / (costLp * chipsPerLpEarned));
            Assert.Equal(1m, lpPerChip);
        }

        [Fact]
        public void TheSeedCatalog_IsValid_AndLpNeverFlowsBack()
        {
            var cfg = ExchangeCatalog.Defaults();
            Assert.Null(ExchangeCatalog.Validate(cfg));

            // LP is exchangeable one way only — the validator refuses the reverse, so no admin can author chips → LP.
            var backwards = new ExchangeCatalogConfig
            {
                Pairs = { new ExchangePairDef { Key = "chips_lp", FromCurrency = "Chips", ToCurrency = "Lp", FromPerUnit = 100m, Step = 1m, MinTo = 1m, Enabled = true } },
            };
            Assert.Contains("can never be exchanged FOR", ExchangeCatalog.Validate(backwards));
        }

        [Fact]
        public void TheDisabledPair_CannotBeUsedByAPlayer()
        {
            var pair = ExchangeCatalog.Defaults().Find("lp_chips");
            Assert.Equal("This exchange is not available.",
                ExchangeCatalog.Refusal(pair, pair.MinTo, 0, 0, 99, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)));
        }

        // ---- the currency itself ----

        [Fact]
        public void Lp_IsAWalletCurrency_AndNotWagerable()
        {
            Assert.Equal(6, (int)CurrencyType.Lp);
            Assert.False(Khela.Game.Services.Wallet.WalletService.IsWagerableCurrency(CurrencyType.Lp));
        }
    }
}
