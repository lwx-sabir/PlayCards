using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Khela.Game.Services.Loyalty
{
    /// <summary>
    /// One-shot: MOVE every player's Loyalty Points from <see cref="UserProfile.LoyaltyPoints"/> into the
    /// <see cref="CurrencyType.Lp"/> wallet (docs/VIP_SPEC.md §3), leaving the column at 0 forever.
    ///
    /// A MOVE, never a copy — a copy would leave the same LP spendable from two places for as long as any reader still
    /// looked at the column. The two halves are therefore ONE transaction per player: credit the wallet, then zero the
    /// column only while it still holds exactly the amount that was credited. If it moved under us the transaction is
    /// ROLLED BACK — which un-does the credit and frees its correlation id — and the player is retried from a fresh
    /// snapshot. Doing the halves independently is what destroys money: the wallet is idempotent on the correlation id,
    /// so a second credit for a larger amount is a no-op, while the zeroing is not — the difference would vanish with no
    /// ledger row at all.
    ///
    /// Every player gets their OWN DI scope. <c>WalletService</c> leaves its transaction open when it throws, so a failure
    /// poisons the <see cref="AppDbContext"/> it ran on; a shared context would then silently enlist — and discard — every
    /// later player in the batch. A scope per player throws the poisoned context away with the row that poisoned it.
    ///
    /// Guarded so one runner works at a time and a finished migration never runs again: a short LEASE while working
    /// (released if the run does not finish, so an interrupted deploy resumes on the next boot rather than stranding
    /// players with 0 spendable LP) and a permanent DONE key when it completes.
    /// </summary>
    public sealed class LoyaltyWalletMigrationService : BackgroundService
    {
        private const string LeaseKey = "khela:migrate:lp-to-wallet:lease";
        private const string DoneKey = "khela:migrate:lp-to-wallet:done";
        private const int BatchSize = 200;
        private const int MaxAttemptsPerPlayer = 3;

        private readonly IServiceScopeFactory _scopes;
        private readonly IRedisService _redis;
        private readonly ILogger<LoyaltyWalletMigrationService> _logger;

        public LoyaltyWalletMigrationService(IServiceScopeFactory scopes, IRedisService redis, ILogger<LoyaltyWalletMigrationService> logger)
        {
            _scopes = scopes; _redis = redis; _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { return; }

            bool leased = false;
            try
            {
                var db = _redis.GetDatabase();
                if (await db.KeyExistsAsync(DoneKey)) return;   // finished on an earlier boot — never run again
                // A LEASE, not a one-way latch: if this run dies the lease expires (or is released below) and the next
                // boot resumes. Only a completed run writes the permanent DONE key.
                if (!await db.StringSetAsync(LeaseKey, Environment.MachineName, TimeSpan.FromMinutes(30), When.NotExists))
                    return;
                leased = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LP migration] no Redis guard available — skipping this boot.");
                return;
            }

            bool complete = false;
            try
            {
                complete = await RunAsync(stoppingToken);
                if (complete)
                {
                    try { await _redis.GetDatabase().StringSetAsync(DoneKey, DateTime.UtcNow.ToString("O")); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[LP migration] finished but could not mark it done; a later boot will find nothing to move."); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "[LP migration] failed; it will be retried on a later boot."); }
            finally
            {
                // Release the lease whatever happened. Left held, an interrupted run would block every later boot for the
                // whole TTL and leave unmigrated players looking at 0 spendable LP.
                if (leased)
                {
                    try { await _redis.GetDatabase().KeyDeleteAsync(LeaseKey); } catch { /* it expires anyway */ }
                }
            }
        }

        /// <summary>Returns true when every player was moved (nothing left with a positive column).</summary>
        private async Task<bool> RunAsync(CancellationToken ct)
        {
            long moved = 0, players = 0, skipped = 0;
            Guid after = Guid.Empty;

            while (!ct.IsCancellationRequested)
            {
                // KEYSET paging: a player we could not move must not re-fill the next page, or the loop makes no progress.
                var batch = await ReadBatchAsync(after, ct);
                if (batch.Count == 0) break;
                after = batch[batch.Count - 1].UserId;

                foreach (var row in batch)
                {
                    if (ct.IsCancellationRequested) return false;
                    var result = await MoveOneAsync(row.UserId, ct);
                    if (result > 0) { moved += result; players++; }
                    else if (result < 0) skipped++;
                }
            }

            if (players > 0 || skipped > 0)
                _logger.LogInformation("[LP migration] moved {Lp} LP for {Players} player(s); {Skipped} left for a later boot.", moved, players, skipped);
            return skipped == 0 && !ct.IsCancellationRequested;
        }

        private async Task<System.Collections.Generic.List<ProfileLp>> ReadBatchAsync(Guid after, CancellationToken ct)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.UserProfiles.AsNoTracking()
                .Where(p => p.LoyaltyPoints > 0 && p.UserId.CompareTo(after) > 0)
                .OrderBy(p => p.UserId)
                .Select(p => new ProfileLp { UserId = p.UserId })
                .Take(BatchSize)
                .ToListAsync(ct);
        }

        /// <summary>Move one player. Returns the LP moved, 0 if there was nothing to move, or -1 if it must be retried later.</summary>
        private async Task<long> MoveOneAsync(Guid userId, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= MaxAttemptsPerPlayer; attempt++)
            {
                // A scope — and so a DbContext — per ATTEMPT: WalletService leaves its transaction open when it throws,
                // and a context in that state would enlist (and then discard) everything that followed it.
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();

                var snapshot = await db.UserProfiles.AsNoTracking()
                    .Where(p => p.UserId == userId).Select(p => (long?)p.LoyaltyPoints).FirstOrDefaultAsync(ct);
                if (snapshot is null or <= 0L) return 0;
                var amount = snapshot.Value;

                await using var tx = await db.Database.BeginTransactionAsync(ct);
                try
                {
                    // Credit joins THIS transaction (WalletService only owns one when there is no ambient), so a rollback
                    // un-does it and frees the correlation id for the retry.
                    await wallet.CreditAsync(userId.ToString(), CurrencyType.Lp, amount, TransactionType.AdminAdjustment,
                        $"lpmig:{userId:N}", new WalletContext { Description = "Loyalty Points moved to the wallet" });

                    var zeroed = await db.UserProfiles
                        .Where(p => p.UserId == userId && p.LoyaltyPoints == amount)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.LoyaltyPoints, 0L), ct);

                    if (zeroed != 1)
                    {
                        // It moved under us. Undo the credit and re-read — never leave a credit whose column was not cleared.
                        await tx.RollbackAsync(ct);
                        continue;
                    }

                    await tx.CommitAsync(ct);
                    return amount;
                }
                catch (Exception ex)
                {
                    try { await tx.RollbackAsync(ct); } catch { /* the scope's disposal rolls back anyway */ }
                    _logger.LogError(ex, "[LP migration] {UserId} attempt {Attempt} failed.", userId, attempt);
                    if (attempt == MaxAttemptsPerPlayer) return -1;
                }
            }

            _logger.LogWarning("[LP migration] {UserId} kept changing under the move; left for a later boot.", userId);
            return -1;
        }

        private sealed class ProfileLp { public Guid UserId { get; set; } }
    }
}
