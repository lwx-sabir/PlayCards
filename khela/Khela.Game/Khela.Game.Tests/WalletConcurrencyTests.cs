using System;
using System.Linq;
using System.Threading.Tasks;
using Khela.Game.Database.Models;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// The wallet's real-money guarantees, against a REAL MySQL — none of these can be shown any other way, because
    /// they ARE database behaviour: the <c>SELECT … FOR UPDATE</c> row lock in <c>WalletService.ApplyAsync</c>,
    /// idempotency on (wallet, correlation id), and the never-negative invariant under concurrency.
    ///
    /// Every test uses a fresh user id, so they neither collide nor need teardown.
    /// </summary>
    [Collection("khela-db")]
    public class WalletConcurrencyTests
    {
        private readonly KhelaDbFixture _fx;
        public WalletConcurrencyTests(KhelaDbFixture fx) => _fx = fx;

        private static string NewUser() => Guid.NewGuid().ToString();

        private async Task<string> FundedUserAsync(decimal chips)
        {
            var user = NewUser();
            using var stack = _fx.NewStack();
            await stack.Wallet.CreditAsync(user, CurrencyType.Chips, chips, TransactionType.AdminAdjustment, $"seed:{user}");
            return user;
        }

        // ---- the row lock ----

        [Fact]
        public async Task ConcurrentDebitsCannotOverdraw()
        {
            // THE test the FOR UPDATE lock exists for: 20 requests race to take 100 from a wallet holding 1000.
            // Without the lock, read-modify-write interleaving lets more than 10 through and the balance goes
            // negative — the one thing a money ledger may never do.
            var user = await FundedUserAsync(1000m);

            var attempts = Enumerable.Range(0, 20).Select(async i =>
            {
                using var stack = _fx.NewStack();
                try
                {
                    await stack.Wallet.DebitAsync(user, CurrencyType.Chips, 100m, TransactionType.Bet, $"bet:{user}:{i}");
                    return true;
                }
                catch (InsufficientFundsException) { return false; }
            });
            var results = await Task.WhenAll(attempts);

            using var check = _fx.NewStack();
            var balance = await check.Wallet.GetBalanceAsync(user, CurrencyType.Chips);

            Assert.Equal(10, results.Count(ok => ok));      // exactly ten could be afforded
            Assert.Equal(0m, balance);                      // …and not one chip more left the wallet
            Assert.True(balance >= 0m);
        }

        [Fact]
        public async Task ConcurrentCreditsNeverLoseAnUpdate()
        {
            // The mirror image: 25 concurrent credits with distinct correlation ids must all land. A lost update
            // here would silently swallow a player's winnings.
            var user = NewUser();

            await Task.WhenAll(Enumerable.Range(0, 25).Select(async i =>
            {
                using var stack = _fx.NewStack();
                await stack.Wallet.CreditAsync(user, CurrencyType.Chips, 40m, TransactionType.Win, $"win:{user}:{i}");
            }));

            using var check = _fx.NewStack();
            Assert.Equal(1000m, await check.Wallet.GetBalanceAsync(user, CurrencyType.Chips));
            Assert.Equal(25, await check.Db.WalletTransactions.CountAsync(t => t.CorrelationId.StartsWith($"win:{user}:")));
        }

        // ---- idempotency ----

        [Fact]
        public async Task TheSameCorrelationIdCreditsOnce_EvenConcurrently()
        {
            // A retried request (flaky network, impatient client, replayed webhook) must never pay twice.
            var user = NewUser();

            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                using var stack = _fx.NewStack();
                try { await stack.Wallet.CreditAsync(user, CurrencyType.Chips, 500m, TransactionType.Bonus, $"once:{user}"); }
                catch (DbUpdateException) { /* lost the unique-index race; the winner's credit stands */ }
            }));

            using var check = _fx.NewStack();
            Assert.Equal(500m, await check.Wallet.GetBalanceAsync(user, CurrencyType.Chips));
            Assert.Equal(1, await check.Db.WalletTransactions.CountAsync(t => t.CorrelationId == $"once:{user}"));
        }

        [Fact]
        public async Task ARepeatedDebitReturnsTheOriginalWithoutChargingAgain()
        {
            var user = await FundedUserAsync(300m);
            using var stack = _fx.NewStack();

            var first = await stack.Wallet.DebitAsync(user, CurrencyType.Chips, 100m, TransactionType.Bet, $"stake:{user}");
            var replay = await stack.Wallet.DebitAsync(user, CurrencyType.Chips, 100m, TransactionType.Bet, $"stake:{user}");

            Assert.Equal(first.TransactionId, replay.TransactionId);
            Assert.Equal(200m, await stack.Wallet.GetBalanceAsync(user, CurrencyType.Chips));
        }

        // ---- invariants the audit relies on ----

        [Fact]
        public async Task EveryRowSatisfiesBalanceBeforePlusAmountEqualsBalanceAfter()
        {
            var user = await FundedUserAsync(500m);
            using var stack = _fx.NewStack();

            await stack.Wallet.DebitAsync(user, CurrencyType.Chips, 120m, TransactionType.Bet, $"a:{user}");
            await stack.Wallet.CreditAsync(user, CurrencyType.Chips, 240m, TransactionType.Win, $"b:{user}");
            await stack.Wallet.DebitAsync(user, CurrencyType.Chips, 20m, TransactionType.Bet, $"c:{user}");

            var wallet = await stack.Db.PlayerWallets.AsNoTracking()
                .SingleAsync(w => w.UserId == Guid.Parse(user) && w.Currency == CurrencyType.Chips);
            var rows = await stack.Db.WalletTransactions.AsNoTracking()
                .Where(t => t.WalletId == wallet.WalletId).OrderBy(t => t.CreatedAt).ToListAsync();

            Assert.All(rows, r => Assert.Equal(r.BalanceAfter, r.BalanceBefore + r.Amount));   // signed-delta ledger
            Assert.Equal(600m, wallet.Balance);                                                // 500 - 120 + 240 - 20
            Assert.Equal(wallet.Balance, rows.Last().BalanceAfter);                            // ledger agrees with the wallet
            Assert.Equal(wallet.Balance, rows.Sum(r => r.Amount));                             // …and with the sum of every delta
        }

        [Fact]
        public async Task DebitingMoreThanTheBalanceThrowsAndWritesNothing()
        {
            var user = await FundedUserAsync(50m);
            using var stack = _fx.NewStack();

            await Assert.ThrowsAsync<InsufficientFundsException>(() =>
                stack.Wallet.DebitAsync(user, CurrencyType.Chips, 51m, TransactionType.Bet, $"over:{user}"));

            Assert.Equal(50m, await stack.Wallet.GetBalanceAsync(user, CurrencyType.Chips));
            Assert.Equal(0, await stack.Db.WalletTransactions.CountAsync(t => t.CorrelationId == $"over:{user}"));
        }

        // ---- rollback ----

        [Fact]
        public async Task RollbackReversesOnce_AndIsIdempotent()
        {
            // The seamless-wallet requirement: a stake taken for a round that was then voided comes back exactly
            // once, as a compensating row rather than by rewriting history.
            var user = await FundedUserAsync(1000m);
            using var stack = _fx.NewStack();
            var stake = $"void:{user}";

            await stack.Wallet.DebitAsync(user, CurrencyType.Chips, 250m, TransactionType.Bet, stake);
            Assert.Equal(750m, await stack.Wallet.GetBalanceAsync(user, CurrencyType.Chips));

            var first = await stack.Wallet.RollbackAsync(user, CurrencyType.Chips, stake);
            var second = await stack.Wallet.RollbackAsync(user, CurrencyType.Chips, stake);

            Assert.NotNull(first);
            Assert.Equal(1000m, await stack.Wallet.GetBalanceAsync(user, CurrencyType.Chips));
            if (second != null) Assert.Equal(first.TransactionId, second.TransactionId);   // the same reversal, not a second one

            var original = await stack.Db.WalletTransactions.AsNoTracking().FirstAsync(t => t.CorrelationId == stake);
            Assert.Equal(TransactionStatus.Reversed, original.Status);                      // history is marked, not mutated
        }

        [Fact]
        public async Task RollingBackSomethingThatNeverHappenedIsANoOp()
        {
            var user = await FundedUserAsync(100m);
            using var stack = _fx.NewStack();

            var result = await stack.Wallet.RollbackAsync(user, CurrencyType.Chips, $"never:{user}");

            Assert.Null(result);
            Assert.Equal(100m, await stack.Wallet.GetBalanceAsync(user, CurrencyType.Chips));
        }
    }
}
