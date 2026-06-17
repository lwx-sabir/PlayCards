using System;
using System.Threading.Tasks;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the NON-NEGOTIABLE legal guardrail (CLAUDE.md rule #2): ONLY Chips and Coins may be
    /// wagered. Every other currency — Gems, the Phase-2 Tokens, and the new cosmetics currency Kash —
    /// must be rejected at the wallet boundary for Bet/Win. A non-wagerable currency that ever became
    /// wagerable would turn the social casino into real-money gambling, so this is regression-locked.
    ///
    /// The wager guard, the amount check and the correlation-id check all run BEFORE any database
    /// access in WalletService.ApplyAsync, so a provider-less AppDbContext is enough — these tests
    /// never touch MySQL.
    /// </summary>
    public class WalletGuardrailTests
    {
        private static WalletService NewService()
        {
            // No provider configured: the guard throws before any query/transaction runs.
            var options = new DbContextOptionsBuilder<AppDbContext>().Options;
            return new WalletService(new AppDbContext(options), NullLogger<WalletService>.Instance);
        }

        private static string AnyUser() => Guid.NewGuid().ToString();

        [Theory]
        [InlineData(CurrencyType.Chips, true)]
        [InlineData(CurrencyType.Coins, true)]
        [InlineData(CurrencyType.Gems, false)]
        [InlineData(CurrencyType.Tokens, false)]
        [InlineData(CurrencyType.Kash, false)]
        public void IsWagerableCurrency_OnlyChipsAndCoins(CurrencyType currency, bool expected)
        {
            Assert.Equal(expected, WalletService.IsWagerableCurrency(currency));
        }

        [Theory]
        [InlineData(CurrencyType.Kash)]
        [InlineData(CurrencyType.Gems)]
        [InlineData(CurrencyType.Tokens)]
        public async Task Bet_OnNonWagerableCurrency_Throws(CurrencyType currency)
        {
            var svc = NewService();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.DebitAsync(AnyUser(), currency, 100m, TransactionType.Bet, "corr-bet"));
        }

        [Theory]
        [InlineData(CurrencyType.Kash)]
        [InlineData(CurrencyType.Gems)]
        [InlineData(CurrencyType.Tokens)]
        public async Task Win_OnNonWagerableCurrency_Throws(CurrencyType currency)
        {
            var svc = NewService();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreditAsync(AnyUser(), currency, 100m, TransactionType.Win, "corr-win"));
        }

        [Fact]
        public async Task Debit_NonPositiveAmount_Throws()
        {
            var svc = NewService();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                svc.DebitAsync(AnyUser(), CurrencyType.Chips, 0m, TransactionType.Bet, "corr"));
        }

        [Fact]
        public async Task Credit_MissingCorrelationId_Throws()
        {
            var svc = NewService();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                svc.CreditAsync(AnyUser(), CurrencyType.Chips, 100m, TransactionType.Bonus, ""));
        }
    }
}
