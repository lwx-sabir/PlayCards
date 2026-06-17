using System;
using System.Threading.Tasks;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Wallet
{
    /// <inheritdoc cref="IWalletService"/>
    public class WalletService : IWalletService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<WalletService> _logger;

        public WalletService(AppDbContext db, ILogger<WalletService> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>Static counterpart to <see cref="IsWagerable"/> for use without an instance.</summary>
        public static bool IsWagerableCurrency(CurrencyType currency)
            => currency == CurrencyType.Chips || currency == CurrencyType.Coins;

        public bool IsWagerable(CurrencyType currency) => IsWagerableCurrency(currency);

        public async Task<PlayerWallet> GetOrCreateWalletAsync(string userId, CurrencyType currency)
        {
            var uid = ParseUserId(userId);

            var wallet = await _db.PlayerWallets
                .FirstOrDefaultAsync(w => w.UserId == uid && w.Currency == currency);
            if (wallet != null) return wallet;

            wallet = new PlayerWallet { UserId = uid, Currency = currency };
            _db.PlayerWallets.Add(wallet);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Lost the race against a concurrent create (unique index UserId+Currency). Reload.
                _db.Entry(wallet).State = EntityState.Detached;
                wallet = await _db.PlayerWallets
                    .FirstAsync(w => w.UserId == uid && w.Currency == currency);
            }

            return wallet;
        }

        public async Task<decimal> GetBalanceAsync(string userId, CurrencyType currency)
        {
            var uid = ParseUserId(userId);
            var wallet = await _db.PlayerWallets.AsNoTracking()
                .FirstOrDefaultAsync(w => w.UserId == uid && w.Currency == currency);
            return wallet?.Balance ?? 0m;
        }

        public Task<WalletTransaction> CreditAsync(string userId, CurrencyType currency, decimal amount,
            TransactionType type, string correlationId, WalletContext context = null)
        {
            if (amount <= 0m)
                throw new ArgumentOutOfRangeException(nameof(amount), "Credit amount must be positive.");
            return ApplyAsync(userId, currency, amount, type, correlationId, context);
        }

        public Task<WalletTransaction> DebitAsync(string userId, CurrencyType currency, decimal amount,
            TransactionType type, string correlationId, WalletContext context = null)
        {
            if (amount <= 0m)
                throw new ArgumentOutOfRangeException(nameof(amount), "Debit amount must be positive.");
            return ApplyAsync(userId, currency, -amount, type, correlationId, context);
        }

        /// <summary>
        /// Core primitive: applies a signed delta to a wallet and records the ledger row atomically.
        /// Concurrency is handled with a pessimistic <c>SELECT ... FOR UPDATE</c> row lock, so two
        /// simultaneous calls on the same wallet serialise rather than race. Idempotency is enforced
        /// by the unique (WalletId, CorrelationId) index: a repeated correlation id returns the
        /// original transaction without applying it twice.
        /// </summary>
        private async Task<WalletTransaction> ApplyAsync(string userId, CurrencyType currency,
            decimal signedAmount, TransactionType type, string correlationId, WalletContext context)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                throw new ArgumentException("A correlation id is required for idempotency.", nameof(correlationId));

            // Legal/integrity guard: a tradeable currency must never be bet or won at a table.
            if ((type == TransactionType.Bet || type == TransactionType.Win) && !IsWagerableCurrency(currency))
                throw new InvalidOperationException(
                    $"Currency '{currency}' is not wagerable; only Chips and Coins may be bet or won at a table.");

            // Ensure the wallet row exists before opening the money transaction.
            var wallet = await GetOrCreateWalletAsync(userId, currency);
            var walletId = wallet.WalletId;

            await using var dbTx = await _db.Database.BeginTransactionAsync();

            // Pessimistic lock: serialise concurrent writers to this wallet row until commit.
            var locked = await _db.PlayerWallets
                .FromSqlInterpolated($"SELECT * FROM `PlayerWallets` WHERE `WalletId` = {walletId} FOR UPDATE")
                .SingleAsync();

            // Idempotency check, performed while holding the row lock.
            var existing = await _db.WalletTransactions
                .FirstOrDefaultAsync(t => t.WalletId == walletId && t.CorrelationId == correlationId);
            if (existing != null)
            {
                await dbTx.CommitAsync();
                return existing;
            }

            if (locked.IsLocked)
                throw new WalletLockedException(walletId);

            var before = locked.Balance;
            var after = before + signedAmount;
            if (after < 0m)
                throw new InsufficientFundsException(walletId, currency, before, -signedAmount);

            var now = DateTime.UtcNow;
            locked.Balance = after;
            locked.LastUpdated = now;

            var txn = new WalletTransaction
            {
                WalletId = walletId,
                Amount = signedAmount,            // signed delta: BalanceBefore + Amount == BalanceAfter
                Type = type,
                Status = TransactionStatus.Completed,
                GameId = context?.GameId,
                Description = context?.Description,
                CorrelationId = correlationId,
                ExternalRef = context?.ExternalRef,
                TableId = context?.TableId,
                RoundId = context?.RoundId,
                BalanceBefore = before,
                BalanceAfter = after,
                MetadataJson = context?.MetadataJson,
                CreatedAt = now,
                CompletedAt = now
            };
            _db.WalletTransactions.Add(txn);

            await _db.SaveChangesAsync();
            await dbTx.CommitAsync();

            _logger.LogInformation(
                "Wallet {WalletId}: {Type} {Amount} {Currency} ({Before} -> {After}) corr={CorrelationId}",
                walletId, type, signedAmount, currency, before, after, correlationId);

            return txn;
        }

        private static Guid ParseUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User id is required.", nameof(userId));
            if (!Guid.TryParse(userId, out var uid))
                throw new ArgumentException($"User id '{userId}' is not a valid GUID.", nameof(userId));
            return uid;
        }
    }
}
