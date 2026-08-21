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

            var uid = ParseUserId(userId);

            // Was this wallet ALREADY read in this request, before the lock? Asked here, before the locking query runs,
            // because that query tracks the row itself. Purely local — it costs nothing.
            bool preTracked = _db.ChangeTracker.Entries<PlayerWallet>()
                .Any(e => e.Entity != null && e.Entity.UserId == uid && e.Entity.Currency == currency);

            // JOIN the caller's transaction when there is one, rather than opening a second.
            //
            // A payout is several of these in a row — chips, then Kash, then XP — and a private transaction each costs
            // a BEGIN and a COMMIT per currency on top of the actual work. Over a link with any latency that is most
            // of the request. Sharing the caller's transaction also makes the whole payout atomic, which the
            // reserve-then-grant dance was only approximating. Rows are still flushed here; only the COMMIT moves out,
            // so a caller cannot lose money by forgetting to save.
            bool ownsTx = _db.Database.CurrentTransaction == null;
            var dbTx = ownsTx ? await _db.Database.BeginTransactionAsync() : null;

            // Pessimistic lock, taken on (user, currency) — a unique index, so this is one record lock — rather than on
            // a WalletId read a moment earlier. Reading the row first only to learn its id costs a round trip AND
            // leaves EF tracking a pre-lock copy, which then has to be reloaded: a second wasted trip before the
            // balance arithmetic is safe to do.
            var locked = await LockWalletAsync(uid, currency);

            if (locked == null)
            {
                // NO WALLET YET — and creating it inside this transaction would be a trap. Twenty-five first-ever
                // credits racing each other would each hold a transaction open while queuing on the unique index,
                // and they time out waiting rather than serialising. So step OUT, create it in its own short
                // transaction (the index settles the race), and start the money transaction over.
                if (ownsTx)
                {
                    await dbTx.RollbackAsync();
                    await dbTx.DisposeAsync();

                    await GetOrCreateWalletAsync(userId, currency);
                    preTracked = true;                     // it is tracked now, so the lock below needs its reload

                    dbTx = await _db.Database.BeginTransactionAsync();
                }
                else
                {
                    // The caller owns the transaction, so there is no stepping out. Create in place: a caller-scoped
                    // payout is one player's claim, not a crowd of first-credits racing for the same row.
                    _db.PlayerWallets.Add(new PlayerWallet { UserId = uid, Currency = currency });
                    await _db.SaveChangesAsync();
                    preTracked = true;
                }

                locked = await LockWalletAsync(uid, currency);
                if (locked == null)
                    throw new InvalidOperationException($"Wallet for {currency} could not be created for {userId}.");
            }

            if (preTracked)
            {
                // Something in this request read the wallet before the lock. EF's identity resolution hands back that
                // pre-lock instance and DISCARDS what the locking query just read, so the arithmetic would run on a
                // stale balance — if another writer committed while we waited for the lock, we'd compute from the old
                // one. Only in that case is a reload worth its round trip.
                await _db.Entry(locked).ReloadAsync();
            }

            var walletId = locked.WalletId;

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

                if (dbTx != null) await dbTx.CommitAsync();
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
            if (dbTx != null) await dbTx.CommitAsync();   // an ambient transaction is the caller's to commit

            _logger.LogInformation(
                "Wallet {WalletId}: {Type} {Amount} {Currency} ({Before} -> {After}) corr={CorrelationId}",
                walletId, type, signedAmount, currency, before, after, correlationId);

            return txn;
        }

        /// <summary>Take the row lock for one wallet, or null if the player has never held that currency.</summary>
        private Task<PlayerWallet> LockWalletAsync(Guid uid, CurrencyType currency)
            => _db.PlayerWallets
                .FromSqlInterpolated($"SELECT * FROM `PlayerWallets` WHERE `UserId` = {uid} AND `Currency` = {(int)currency} FOR UPDATE")
                .SingleOrDefaultAsync();

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
