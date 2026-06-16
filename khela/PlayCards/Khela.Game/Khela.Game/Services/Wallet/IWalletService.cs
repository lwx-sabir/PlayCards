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
        /// True for currencies that may be bet or won at a table (Chips, Coins). Premium / spend
        /// currencies (Gems, Kash) and the tradeable token (Tokens) are never wagerable — wagering
        /// them would constitute real-money gambling.
        /// </summary>
        bool IsWagerable(CurrencyType currency);
    }
}
