using System;
using System.Threading.Tasks;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Wallet
{
    /// <inheritdoc cref="IWalletService"/>
    public class WalletService : IWalletService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<WalletService> _logger;
        private readonly bool _trackGiftedChips;

        public WalletService(AppDbContext db, ILogger<WalletService> logger, IConfiguration config = null)
        {
            _db = db;
            _logger = logger;
            // The gifted-chip taint is a GAME-LAYER extension (it exists only to deny progression XP to gifted
            // chips). When the progression layer is off, the wallet runs as a pure ledger and never touches the
            // gifted sub-balance — the real-money path is byte-for-byte the original behaviour. A null config
            // (unit tests that construct WalletService directly) defaults the layer ON.
            _trackGiftedChips = config?.GetValue("Progression:Enabled", true) ?? true;
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

        public async Task<IReadOnlyDictionary<CurrencyType, decimal>> GetBalancesAsync(string userId)
        {
            var uid = ParseUserId(userId);
            return await _db.PlayerWallets.AsNoTracking()
                .Where(w => w.UserId == uid)
                .ToDictionaryAsync(w => w.Currency, w => w.Balance);
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

            // …and then FORCE the freshly-locked values into the entity. The FOR UPDATE above really does take the
            // row lock, but the wallet was already tracked by GetOrCreateWalletAsync (read BEFORE the lock), and EF's
            // identity resolution returns that tracked instance and DISCARDS the values the locking query just read.
            // Without this reload the balance arithmetic runs on a pre-lock snapshot: if another writer committed
            // while we waited for the lock, we'd compute from a stale balance. The RowVersion token would catch it as
            // a concurrency exception rather than corruption, but a failed money op is still a failed money op — and
            // the pessimistic serialisation this method documents would be a fiction.
            await _db.Entry(locked).ReloadAsync();

            // Idempotency check, performed while holding the row lock.
            var existing = await _db.WalletTransactions
                .FirstOrDefaultAsync(t => t.WalletId == walletId && t.CorrelationId == correlationId);
            if (existing != null)
            {
                // A ROLLED-BACK movement must never be replayed as a success. Returning it here would tell the caller
                // "already applied" for money that has since been returned — e.g. a stake is voided, the game retries
                // the debit with the same id, gets the reversed row back, and deals a hand nobody actually paid for.
                // The id is spent: the unique index means it can't be re-applied either, so this has to be an error.
                if (existing.Status == TransactionStatus.Reversed)
                    throw new InvalidOperationException(
                        $"Correlation id '{correlationId}' was rolled back and cannot be re-used; issue a new id.");

                await dbTx.CommitAsync();
                return existing;
            }

            if (locked.IsLocked)
                throw new WalletLockedException(walletId);

            var before = locked.Balance;
            var giftedBefore = locked.GiftedBalance;
            var after = before + signedAmount;
            if (after < 0m)
                throw new InsufficientFundsException(walletId, currency, before, -signedAmount);

            // Track the TAINTED (gifted) slice of the balance; the earned balance is the remainder.
            //  Debit  → spend EARNED first (= Balance - GiftedBalance); only dip into gifted once earned runs out.
            //  Credit → clean by default; the caller pins the tainted slice via WalletContext.CreditGiftedAmount
            //           (a player gift = full amount; a bet payout = gross × giftedStakeRatio so winnings keep
            //           the stake's gifted fraction and gifted chips can't be laundered clean).
            // The arithmetic lives in WalletBuckets so it's unit-testable without the DB/lock/transaction.
            // GATED: when the game layer is off, the wallet is a pure ledger — the gifted slice stays 0/untouched.
            var giftedDelta = _trackGiftedChips
                ? WalletBuckets.GiftedDelta(locked.Balance, locked.GiftedBalance, signedAmount, context?.CreditGiftedAmount)
                : 0m;

            var now = DateTime.UtcNow;
            locked.Balance = after;
            locked.GiftedBalance += giftedDelta;   // stays in [0, Balance] by construction
            locked.LastUpdated = now;

            var txn = new WalletTransaction
            {
                WalletId = walletId,
                Amount = signedAmount,            // signed delta: BalanceBefore + Amount == BalanceAfter
                GiftedDelta = giftedDelta,        // tainted slice of Amount; earned slice = Amount - GiftedDelta
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
                GiftedBalanceBefore = giftedBefore,
                GiftedBalanceAfter = giftedBefore + giftedDelta,
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

        /// <inheritdoc/>
        public async Task<WalletTransaction> RollbackAsync(string userId, CurrencyType currency,
            string originalCorrelationId, WalletContext context = null)
        {
            if (string.IsNullOrWhiteSpace(originalCorrelationId))
                throw new ArgumentException("The original correlation id is required.", nameof(originalCorrelationId));

            var wallet = await GetOrCreateWalletAsync(userId, currency);
            var walletId = wallet.WalletId;
            var reversalId = ReversalIdFor(originalCorrelationId);

            await using var dbTx = await _db.Database.BeginTransactionAsync();

            // Same pessimistic lock as ApplyAsync: the original lookup, the duplicate-reversal check and the
            // balance write all have to happen under one lock, or two concurrent rollbacks could both decide
            // there was no reversal yet and refund twice.
            var locked = await _db.PlayerWallets
                .FromSqlInterpolated($"SELECT * FROM `PlayerWallets` WHERE `WalletId` = {walletId} FOR UPDATE")
                .SingleAsync();
            await _db.Entry(locked).ReloadAsync();   // see ApplyAsync: identity resolution would hand back pre-lock values

            // Already reversed? Return that reversal — this is what makes a repeated rollback safe.
            var existingReversal = await _db.WalletTransactions
                .FirstOrDefaultAsync(t => t.WalletId == walletId && t.CorrelationId == reversalId);
            if (existingReversal != null)
            {
                await dbTx.CommitAsync();
                return existingReversal;
            }

            var original = await _db.WalletTransactions
                .FirstOrDefaultAsync(t => t.WalletId == walletId && t.CorrelationId == originalCorrelationId);
            if (original == null)
            {
                // Nothing to reverse. NOTE for a future operator integration: real-money PAMs also expect a
                // rollback for an UNKNOWN bet to be recorded as a void, so a late-arriving bet with that id is
                // then rejected. We don't need that until a licensee drives the wallet; flagged deliberately.
                await dbTx.CommitAsync();
                _logger.LogWarning("Wallet {WalletId}: rollback requested for unknown corr={CorrelationId} — nothing reversed.",
                    walletId, originalCorrelationId);
                return null;
            }

            if (locked.IsLocked) throw new WalletLockedException(walletId);

            // Exact inverse — including the tainted slice, or the earned/gifted split drifts every reversal.
            var before = locked.Balance;
            var giftedBefore = locked.GiftedBalance;
            var signedAmount = -original.Amount;
            var giftedDelta = -original.GiftedDelta;

            var after = before + signedAmount;
            if (after < 0m)
                throw new InsufficientFundsException(walletId, currency, before, -signedAmount);

            var now = DateTime.UtcNow;
            locked.Balance = after;
            // Clamp defensively: a correctly applied original keeps this in range on its own, but a reversal must
            // never be the thing that pushes the tainted slice outside [0, Balance]. Record the delta that was
            // ACTUALLY applied, not the one we intended — otherwise a clamp would move the wallet without moving the
            // ledger, and this row would be the one place in the system where
            // GiftedBalanceBefore + GiftedDelta != GiftedBalanceAfter (and SUM(GiftedDelta) stopped reconciling).
            var giftedAfter = Math.Clamp(giftedBefore + giftedDelta, 0m, after);
            giftedDelta = giftedAfter - giftedBefore;
            locked.GiftedBalance = giftedAfter;
            locked.LastUpdated = now;

            original.Status = TransactionStatus.Reversed;
            original.ReversedAt = now;
            original.UpdatedAt = now;

            var txn = new WalletTransaction
            {
                WalletId = walletId,
                Amount = signedAmount,
                GiftedDelta = giftedDelta,
                Type = TransactionType.Refund,
                Status = TransactionStatus.Completed,
                GameId = context?.GameId ?? original.GameId,
                Description = context?.Description ?? $"Rollback of {originalCorrelationId}",
                CorrelationId = reversalId,
                ExternalRef = original.TransactionId.ToString(),   // ties the pair together for audit
                TableId = context?.TableId ?? original.TableId,
                RoundId = context?.RoundId ?? original.RoundId,
                BalanceBefore = before,
                BalanceAfter = after,
                GiftedBalanceBefore = giftedBefore,
                GiftedBalanceAfter = locked.GiftedBalance,
                MetadataJson = context?.MetadataJson,
                CreatedAt = now,
                CompletedAt = now
            };
            _db.WalletTransactions.Add(txn);

            await _db.SaveChangesAsync();
            await dbTx.CommitAsync();

            _logger.LogInformation(
                "Wallet {WalletId}: ROLLBACK of {OriginalCorr} ({Amount} {Currency}, {Before} -> {After}) corr={CorrelationId}",
                walletId, originalCorrelationId, signedAmount, currency, before, after, reversalId);

            return txn;
        }

        /// <summary>
        /// Deterministic correlation id for the reversal of <paramref name="originalCorrelationId"/> — the same
        /// input always yields the same id, which is what makes rollback idempotent via the unique
        /// (WalletId, CorrelationId) index. Falls back to a hash when the prefixed id would exceed the column's
        /// 64 chars, so a long game correlation id can still be reversed without truncation collisions.
        /// </summary>
        public static string ReversalIdFor(string originalCorrelationId)
        {
            const string prefix = "rb:";
            var direct = prefix + originalCorrelationId;
            if (direct.Length <= 64) return direct;

            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(originalCorrelationId));
            return "rb#" + Convert.ToHexString(hash).Substring(0, 61);   // "rb#" (3) + 61 hex = the column's 64
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
