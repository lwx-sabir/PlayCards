using System.Collections.Generic;
using System.Linq;
using Khela.Common.Leaderboards;
using Khela.Game.Database.Models;
using Khela.Game.Services.Rewards;
using Khela.Game.Services.Vip;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Seasons and the badge ladder (docs/VIP_SPEC.md §2). The model rests on ONE fact: within a season SP only ever goes
    /// UP — accrual credits the wallet and nothing debits it until the roll. So the band of the balance is the tier the
    /// player climbed to, mid-season demotion cannot happen, and the roll is the only demotion. These pin the arithmetic
    /// that follows from that, and the guardrails on the reset ladder.
    /// </summary>
    public class SeasonTierTests
    {
        private static VipConfig Base() => new VipConfig();

        // ---- the reset ladder ----

        [Fact]
        public void ResetTo_SendsEveryTierDown_AndLeavesTheFloorAlone()
        {
            var c = Base();
            Assert.Equal((int)VipTier.None, VipMath.ResetTo(c, (int)VipTier.None));       // nothing to lose
            Assert.Equal((int)VipTier.Bronze, VipMath.ResetTo(c, (int)VipTier.Bronze));   // the free floor stays

            // Everything above drops, and never below Bronze.
            for (int t = (int)VipTier.Silver; t <= (int)VipTier.BlackDiamond; t++)
            {
                var to = VipMath.ResetTo(c, t);
                Assert.True(to < t, $"tier {t} must fall");
                Assert.True(to >= (int)VipTier.Bronze, $"tier {t} must not fall below Bronze");
            }
            Assert.Equal((int)VipTier.Bronze, VipMath.ResetTo(c, (int)VipTier.Silver));
            Assert.Equal((int)VipTier.RoyalDiamond, VipMath.ResetTo(c, (int)VipTier.BlackDiamond));
        }

        [Fact]
        public void AResetThatPROMOTES_IsRefusedWhole()
        {
            var b = Base();
            var rows = VipConfig.TierRows(b);
            // Silver resetting to Gold would mean finishing a season made you better off doing nothing.
            rows[(int)VipTier.Silver] = new VipConfig.TierRow
            {
                SpThreshold = rows[(int)VipTier.Silver].SpThreshold, SpendFloorUsd = 0m,
                BonusPct = rows[(int)VipTier.Silver].BonusPct,
                ResetTo = (int)VipTier.Gold,
            };
            Assert.Equal(b.TierResetTo, VipConfig.ParseTiers(VipConfig.SerializeTiers(rows), b).TierResetTo);
        }

        [Fact]
        public void TheResetLadder_RoundTripsThroughTheAdminDocument()
        {
            var b = Base();
            var rows = VipConfig.TierRows(b);
            Assert.Equal(8, rows.Count);
            Assert.Equal((int)VipTier.RoyalDiamond, rows[(int)VipTier.BlackDiamond].ResetTo);

            var parsed = VipConfig.ParseTiers(VipConfig.SerializeTiers(rows), b);
            Assert.Equal(b.TierResetTo, parsed.TierResetTo);

            // An admin can retune it — everyone back to Bronze, say — and the game runs what was saved.
            var harsh = rows.Select((r, i) => new VipConfig.TierRow
            {
                SpThreshold = r.SpThreshold, SpendFloorUsd = r.SpendFloorUsd, BonusPct = r.BonusPct,
                ResetTo = i <= (int)VipTier.Bronze ? i : (int)VipTier.Bronze,
            }).ToList();
            var cfg = VipConfig.Overlay(b, new Dictionary<string, string> { ["Vip:Tiers"] = VipConfig.SerializeTiers(harsh) });
            Assert.Equal((int)VipTier.Bronze, VipMath.ResetTo(cfg, (int)VipTier.BlackDiamond));
        }

        // ---- what a roll does ----

        [Fact]
        public void ARoll_LandsThePlayerExactlyOnTheNewTiersBar()
        {
            // The whole reset: climbed tier -> ResetTo -> SP set to that tier's bar. A Diamond player (6.25M SP) ends the
            // season at Platinum with exactly Platinum's bar — still ranked, with the next climb ahead of them.
            var c = Base();
            long balance = 7_000_000;                                    // somewhere above Diamond's bar
            var climbed = VipMath.ResolveBand(balance, decimal.MaxValue, 99, c);
            Assert.Equal(VipTier.Diamond, climbed);

            var resetTo = (VipTier)VipMath.ResetTo(c, (int)climbed);
            Assert.Equal(VipTier.Platinum, resetTo);

            var target = VipMath.SpBar(c, (int)resetTo);
            Assert.Equal(1_250_000L, target);
            Assert.Equal(-5_750_000L, target - balance);                 // the roll's ledger movement: a debit

            // And the new balance bands to exactly the new tier — not one notch under it.
            Assert.Equal(VipTier.Platinum, VipMath.ResolveBand(target, decimal.MaxValue, 99, c));
        }

        [Fact]
        public void ARoll_NeverPushesAPlayerNegative()
        {
            // The target is the bar of a tier at or below the one the balance already cleared, so the movement is always a
            // debit the balance can cover (or nothing) — a reset can never ask the wallet for more SP than the player has.
            var c = Base();
            for (int t = (int)VipTier.Bronze; t <= (int)VipTier.BlackDiamond; t++)
            {
                long balance = VipMath.SpBar(c, t);
                var climbed = VipMath.ResolveBand(balance, decimal.MaxValue, 99, c);
                var target = VipMath.SpBar(c, VipMath.ResetTo(c, (int)climbed));
                Assert.True(target <= balance, $"tier {t}: reset target {target} exceeds the balance {balance}");
            }
        }

        // ---- the band, now read from a season balance ----

        [Fact]
        public void TheBand_IsTheSeasonBalance_AndNeedsNoSpendAnyMore()
        {
            var c = Base();
            Assert.All(c.SpendFloorsUsd, f => Assert.Equal(0m, f));   // money buys VIP-P, not status

            // With no floors, a free player can reach the top of the ladder on play alone — that is the point.
            Assert.Equal(VipTier.BlackDiamond, VipMath.ResolveBand(150_000_000, 0m, 99, c));
            Assert.Equal(VipTier.Silver, VipMath.ResolveBand(50_000, 0m, 99, c));
            Assert.Equal(VipTier.Bronze, VipMath.ResolveBand(0, 0m, 99, c));

            // Below the entry level there is no tier at all, whatever the SP.
            Assert.Equal(VipTier.None, VipMath.ResolveBand(150_000_000, 0m, c.VipEntryLevel - 1, c));
        }

        // ---- SP the currency ----

        [Fact]
        public void Sp_IsAWalletCurrency_GrantableAndSellable_ButNeverExchanged()
        {
            Assert.Equal(5, (int)CurrencyType.Sp);
            Assert.False(Khela.Game.Services.Wallet.WalletService.IsWagerableCurrency(CurrencyType.Sp));
            Assert.True(RewardCurrencies.IsGrantable(CurrencyType.Sp));    // packs, rewards, the world
            Assert.True(RewardCurrencies.IsSellable(CurrencyType.Sp));
            Assert.False(RewardCurrencies.IsExchangeableFrom(CurrencyType.Sp));
            Assert.False(RewardCurrencies.IsExchangeableTo(CurrencyType.Sp));
        }

        [Fact]
        public void Seasons_DefaultToLifetime_SoTheFeatureShipsOff()
        {
            // Season:LengthDays is 0 until it is set, and a season with no end never rolls — nobody is reset by surprise.
            var season = new Season { Index = 1, EndsAtUtc = null };
            Assert.Null(season.EndsAtUtc);
            Assert.Equal(SeasonStatus.Open, season.Status);
        }
    }
}
