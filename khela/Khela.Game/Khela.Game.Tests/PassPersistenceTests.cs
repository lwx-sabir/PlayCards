using System;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Khela.Game.Database.Models;
using Khela.Game.Services.Pass;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// The half of the pass that pure tests can't reach: what actually hits the database. Runs against a REAL MySQL
    /// (see <see cref="KhelaDbFixture"/>) because the properties being proven — a unique index winning a concurrent
    /// race, an interrupted claim being re-driven, credits spent exactly once — are engine behaviour, not logic.
    ///
    /// Every test uses a fresh user id, so they neither collide nor need teardown.
    /// </summary>
    [Collection("khela-db")]
    public class PassPersistenceTests
    {
        private readonly KhelaDbFixture _fx;
        public PassPersistenceTests(KhelaDbFixture fx) => _fx = fx;

        private static Guid NewUser() => Guid.NewGuid();

        /// <summary>Today's node in the default (UTC, no profile row) cycle — the tests have to live on the real calendar.</summary>
        private static int Today => DateTime.UtcNow.Day;

        private static decimal ChipsIn(PassNode node)
            => node.Free.Where(l => l.Kind == RewardKind.Currency && l.Id == "Chips").Sum(l => l.Amount);

        private static PassCycle Cycle()
            => PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), DateTime.UtcNow, TimeZoneInfo.Utc);

        // ---- the ordinary claim ----

        [Fact]
        public async Task ClaimingTodayWritesTheLedgerAndCreditsTheWallet()
        {
            var user = NewUser();
            using var stack = _fx.NewStack();
            var expected = ChipsIn(Cycle().Node(Today));

            var result = await stack.Pass.ClaimAsync(user);

            Assert.True(result.Ok, result.Error);
            Assert.Equal(new[] { Today }, result.ClaimedNodes);

            var claim = await stack.Db.PlayerPassClaims.AsNoTracking()
                .SingleAsync(c => c.UserId == user && c.Node == Today);
            Assert.True(claim.FreeGranted);
            Assert.NotNull(claim.CompletedAt);          // the row is only complete once the payout landed
            Assert.False(claim.GoldenGranted);          // no subscription → the golden half stays owed
            Assert.False(claim.WasAdUnlock);

            Assert.Equal(expected, await stack.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
        }

        [Fact]
        public async Task ThePassKeepsWorkingWithRedisDown()
        {
            // The fixture's IRedisService throws on every call: the config overlay read must fall back to the code
            // defaults rather than take the pass down with the cache.
            var user = NewUser();
            using var stack = _fx.NewStack();

            var state = await stack.Pass.GetStateAsync(user);

            Assert.True(state.Active);
            Assert.Equal(PassCatalog.MonthlyKey, state.PassKey);
            Assert.Equal(Today, state.DayIndex);
        }

        [Fact]
        public async Task ClaimingTwiceNeverPaysTwice()
        {
            var user = NewUser();
            using var stack = _fx.NewStack();

            var first = await stack.Pass.ClaimAsync(user);
            var balance = await stack.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips);
            var second = await stack.Pass.ClaimAsync(user, node: Today);

            Assert.True(first.Ok);
            Assert.False(second.Ok);
            Assert.Equal("Already claimed.", second.Error);
            Assert.Equal(balance, await stack.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
            Assert.Equal(1, await stack.Db.PlayerPassClaims.CountAsync(c => c.UserId == user));
        }

        [Fact]
        public async Task TwoConcurrentClaimsOfTheSameDayProduceOneRowAndOnePayout()
        {
            // The double-tap / double-request case. Two separate DbContexts race the unique index; the loser must
            // finish the winner's row rather than fail or pay again.
            var user = NewUser();
            using var a = _fx.NewStack();
            using var b = _fx.NewStack();
            var expected = ChipsIn(Cycle().Node(Today));

            var results = await Task.WhenAll(a.Pass.ClaimAsync(user), b.Pass.ClaimAsync(user));

            Assert.Contains(results, r => r.Ok);
            using var check = _fx.NewStack();
            Assert.Equal(1, await check.Db.PlayerPassClaims.CountAsync(c => c.UserId == user && c.Node == Today));
            Assert.Equal(expected, await check.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
        }

        [Fact]
        public async Task AnInterruptedClaimIsFinishedByTheNextCall()
        {
            // Simulate a crash between RESERVE and GRANT: the row exists, nothing was paid, CompletedAt is null.
            var user = NewUser();
            using var seed = _fx.NewStack();
            var cycle = Cycle();
            seed.Db.PlayerPassClaims.Add(new PlayerPassClaim
            {
                UserId = user,
                PassKey = cycle.PassKey,
                CycleKey = cycle.CycleKey,
                Node = Today,
                ClaimedOnUtc = DateTime.UtcNow,
                ClaimedOnLocalDate = DateTime.UtcNow.Date,
                TimeZoneId = "UTC",
                FreeGranted = false,
                GoldenGranted = false,
                CompletedAt = null,
            });
            await seed.Db.SaveChangesAsync();

            using var stack = _fx.NewStack();
            var result = await stack.Pass.ClaimAsync(user);

            Assert.True(result.Ok, result.Error);
            var claim = await stack.Db.PlayerPassClaims.AsNoTracking().SingleAsync(c => c.UserId == user && c.Node == Today);
            Assert.True(claim.FreeGranted);
            Assert.NotNull(claim.CompletedAt);
            Assert.Equal(ChipsIn(cycle.Node(Today)), await stack.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
        }

        // ---- the golden path ----

        [Fact]
        public async Task SubscribingEnqueuesTheGoldenHalfOfDaysAlreadyClaimed_AndOnlyOnce()
        {
            var user = NewUser();
            using var stack = _fx.NewStack();
            var now = DateTime.UtcNow;

            await stack.Pass.ClaimAsync(user);                       // claimed WITHOUT a subscription
            var before = await stack.Db.PlayerPassClaims.AsNoTracking().SingleAsync(c => c.UserId == user);
            Assert.False(before.GoldenGranted);

            var grant = await stack.Pass.GrantGoldenAsync(user, null, "admin", $"test:{Guid.NewGuid():N}",
                now.AddMinutes(-1), now.AddDays(30));
            Assert.True(grant.Ok);
            Assert.True(grant.IsGolden);

            var after = await stack.Db.PlayerPassClaims.AsNoTracking().SingleAsync(c => c.UserId == user);
            Assert.True(after.GoldenGranted);                        // the owed half is now settled…

            var enqueued = await stack.Db.PlayerRewards.AsNoTracking()
                .Where(r => r.UserId == user && r.Source == RewardSource.Pass).ToListAsync();
            Assert.NotEmpty(enqueued);                               // …as INBOX rewards, collected by tapping
            Assert.All(enqueued, r => Assert.Equal(RewardStatus.Pending, r.Status));
            Assert.All(enqueued, r => Assert.StartsWith("pass-retro:", r.IdempotencyKey));

            // A renewal / retry / resubscribe must not pay the same day again.
            await stack.Pass.GrantGoldenAsync(user, null, "admin", $"test:{Guid.NewGuid():N}", now, now.AddDays(60));
            var afterSecond = await stack.Db.PlayerRewards.AsNoTracking()
                .CountAsync(r => r.UserId == user && r.Source == RewardSource.Pass);
            Assert.Equal(enqueued.Count, afterSecond);
        }

        [Fact]
        public async Task ASubscriberGetsBothHalvesInlineOnTheNextClaim()
        {
            var user = NewUser();
            using var stack = _fx.NewStack();
            var now = DateTime.UtcNow;

            await stack.Pass.GrantGoldenAsync(user, null, "admin", $"test:{Guid.NewGuid():N}", now.AddMinutes(-1), now.AddDays(30));
            var result = await stack.Pass.ClaimAsync(user);

            Assert.True(result.Ok, result.Error);
            var claim = await stack.Db.PlayerPassClaims.AsNoTracking().SingleAsync(c => c.UserId == user);
            Assert.True(claim.FreeGranted);
            Assert.True(claim.GoldenGranted);                        // paid straight to the wallet, not the inbox

            var node = Cycle().Node(Today);
            var expected = node.Free.Concat(node.Golden)
                .Where(l => l.Kind == RewardKind.Currency && l.Id == "Chips").Sum(l => l.Amount);
            Assert.Equal(expected, await stack.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
        }

        [Fact]
        public async Task RevokingExpiresUncollectedGoldenRewardsButKeepsCollectedOnes()
        {
            var user = NewUser();
            using var stack = _fx.NewStack();
            var now = DateTime.UtcNow;
            var purchase = $"test:{Guid.NewGuid():N}";

            await stack.Pass.ClaimAsync(user);
            await stack.Pass.GrantGoldenAsync(user, null, "iap", purchase, now.AddMinutes(-1), now.AddDays(30));

            var pending = await stack.Db.PlayerRewards.AsNoTracking()
                .Where(r => r.UserId == user && r.Source == RewardSource.Pass && r.Status == RewardStatus.Pending)
                .ToListAsync();
            Assert.NotEmpty(pending);

            // Collect ONE of them, then refund: the collected chips stay, the rest die with the entitlement.
            var collected = await stack.Rewards.ClaimAsync(user, pending[0].Id);
            Assert.True(collected.Ok);
            var balanceAfterCollect = await stack.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips);

            await stack.Pass.RevokeGoldenAsync(user, null, purchase, "chargeback");

            Assert.False(await stack.Pass.IsGoldenAsync(user, PassCatalog.MonthlyKey, DateTime.UtcNow));
            Assert.Equal(0, await stack.Db.PlayerRewards.CountAsync(r =>
                r.UserId == user && r.Source == RewardSource.Pass && r.Status == RewardStatus.Pending));
            Assert.Equal(balanceAfterCollect, await stack.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
        }

        // ---- ad catch-up ----

        [Fact]
        public async Task AnAdUnlockSpendsExactlyTheCreditsItCosts()
        {
            if (Today < 2) return;   // no missed day exists on the 1st — nothing to buy back

            var user = NewUser();
            using var stack = _fx.NewStack();
            var cycle = Cycle();
            int missed = Today - 1;

            for (int i = 0; i < 2; i++)
                stack.Db.PlayerPassAdUnlocks.Add(new PlayerPassAdUnlock
                {
                    UserId = user,
                    PassKey = cycle.PassKey,
                    CycleKey = cycle.CycleKey,
                    AdTransactionId = $"test-{Guid.NewGuid():N}",
                    Network = "test",
                });
            await stack.Db.SaveChangesAsync();

            var result = await stack.Pass.ClaimAsync(user, node: missed, useAds: true);

            Assert.True(result.Ok, result.Error);
            Assert.Equal(2, result.AdCreditsSpent);

            var claim = await stack.Db.PlayerPassClaims.AsNoTracking().SingleAsync(c => c.UserId == user && c.Node == missed);
            Assert.True(claim.WasAdUnlock);
            Assert.True(claim.FreeGranted);

            var credits = await stack.Db.PlayerPassAdUnlocks.AsNoTracking().Where(a => a.UserId == user).ToListAsync();
            Assert.All(credits, c => Assert.Equal(missed, c.SpentOnNode));
            Assert.Equal(ChipsIn(cycle.Node(missed)), await stack.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
        }

        [Fact]
        public async Task AMissedDayIsRefusedWithoutEnoughCredits_AndNothingIsWritten()
        {
            if (Today < 2) return;

            var user = NewUser();
            using var stack = _fx.NewStack();
            int missed = Today - 1;

            var result = await stack.Pass.ClaimAsync(user, node: missed, useAds: true);

            Assert.False(result.Ok);
            Assert.Contains("ad", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await stack.Db.PlayerPassClaims.CountAsync(c => c.UserId == user));   // no half-written row
            Assert.Equal(0m, await stack.Wallet.GetBalanceAsync(user.ToString(), CurrencyType.Chips));
        }

        [Fact]
        public async Task AFreePlayerCannotBackfillAMissedDayForNothing()
        {
            if (Today < 2) return;

            var user = NewUser();
            using var stack = _fx.NewStack();

            var result = await stack.Pass.ClaimAsync(user, node: Today - 1);

            Assert.False(result.Ok);
            Assert.Equal(0, await stack.Db.PlayerPassClaims.CountAsync(c => c.UserId == user));
        }

        [Fact]
        public async Task ClaimAllTakesEveryFreeDayForASubscriberAndNeverSpendsAdCredits()
        {
            var user = NewUser();
            using var stack = _fx.NewStack();
            var now = DateTime.UtcNow;
            await stack.Pass.GrantGoldenAsync(user, null, "admin", $"test:{Guid.NewGuid():N}", now.AddMinutes(-1), now.AddDays(30));

            var result = await stack.Pass.ClaimAllAsync(user);

            Assert.True(result.Ok, result.Error);
            Assert.Equal(Today, result.ClaimedNodes.Count);                     // days 1..today, all free for Golden
            Assert.Equal(Enumerable.Range(1, Today), result.ClaimedNodes.OrderBy(n => n));
            Assert.Equal(0, result.AdCreditsSpent);
            Assert.Equal(Today, await stack.Db.PlayerPassClaims.CountAsync(c => c.UserId == user));
        }
    }
}
