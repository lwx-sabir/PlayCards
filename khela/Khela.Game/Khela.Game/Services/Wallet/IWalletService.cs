using System.Threading.Tasks;
using Khela.Game.Database.Models;

namespace Khela.Game.Services.Wallet
{
    /// <summary>
    /// Authoritative, idempotent ledger for player wallets. Every balance change is recorded
    /// as a <see cref="WalletTransaction"/> and applied atomically under a per-wallet row lock,
    /// so concurrent bets, wins and purchases cannot corrupt a balance or double-apply.
    ///
    /// Amounts are passed as positive magnitudes to <see cref="DebitAsync"/> / <see cref="CreditAsync"/>;
    /// the ledger stores them as a signed delta so that BalanceBefore + Amount == BalanceAfter.
    /// </summary>
    public interface IWalletService
    {
        /// <summary>Returns the wallet for (user, currency), creating an empty one if none exists.</summary>
        Task<PlayerWallet> GetOrCreateWalletAsync(string userId, CurrencyType currency);

        /// <summary>Reads the current balance for (user, currency). Returns 0 if no wallet exists.</summary>
        Task<decimal> GetBalanceAsync(string userId, CurrencyType currency);

        /// <summary>
        /// Every currency balance for a user in ONE query. The balance HUD is on every screen and the client asks
        /// for it constantly — reading five currencies as five separate round trips costs five times the latency
        /// for the same rows, which is felt directly as UI lag when the database is not on the same machine.
        /// Currencies with no wallet row are simply absent; treat missing as zero.
        /// </summary>
        Task<IReadOnlyDictionary<CurrencyType, decimal>> GetBalancesAsync(string userId);

        /// <summary>
        /// Adds funds to a wallet. <paramref name="correlationId"/> makes the call idempotent:
        /// repeating it with the same id returns the original transaction without crediting twice.
        /// </summary>
        Task<WalletTransaction> CreditAsync(string userId, CurrencyType currency, decimal amount,
            TransactionType type, string correlationId, WalletContext context = null);

        /// <summary>
        /// Removes funds from a wallet. Throws <see cref="InsufficientFundsException"/> if the balance
        /// would go negative. Idempotent on <paramref name="correlationId"/>.
        /// </summary>
        Task<WalletTransaction> DebitAsync(string userId, CurrencyType currency, decimal amount,
            TransactionType type, string correlationId, WalletContext context = null);

        /// <summary>
        /// REVERSES a previously applied transaction, identified by ITS correlation id — the "rollback" /
        /// "cancel" every real-money wallet integration requires: a stake taken for a round that was then
        /// voided, a client that dropped mid-bet, or an operator's account system rejecting a movement after
        /// the fact. Writes a compensating ledger row (<see cref="TransactionType.Refund"/>) rather than
        /// mutating history, and marks the original <see cref="TransactionStatus.Reversed"/>.
        ///
        /// Idempotent: the reversal's own correlation id is DERIVED from the original, so repeating the call
        /// returns the existing reversal instead of paying twice. Reverses the tainted (gifted) slice exactly
        /// as well, so the earned/gifted split can't drift.
        ///
        /// Returns null when there is nothing to reverse (no transaction with that correlation id on that
        /// wallet). Throws <see cref="InsufficientFundsException"/> if reversing a CREDIT the player has
        /// already spent would drive the balance negative — surfaced rather than silently breaking the
        /// never-negative ledger invariant the audit relies on.
        /// </summary>
        Task<WalletTransaction> RollbackAsync(string userId, CurrencyType currency,
            string originalCorrelationId, WalletContext context = null);

        /// <summary>
        /// True for currencies that may be bet or won at a table (Chips, Coins). Premium / spend
        /// currencies (Gems, Kash) and the tradeable token (Tokens) are never wagerable — wagering
        /// them would constitute real-money gambling.
        /// </summary>
        bool IsWagerable(CurrencyType currency);
    }
}
