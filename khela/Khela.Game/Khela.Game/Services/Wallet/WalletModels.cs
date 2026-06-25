using System;
using Khela.Game.Database.Models;

namespace Khela.Game.Services.Wallet
{
    /// <summary>
    /// Optional context attached to a wallet transaction for traceability and dispute
    /// resolution. All fields are optional; supply whatever is meaningful for the source
    /// of the movement (a game round, an in-app purchase, an admin action, etc.).
    /// </summary>
    public sealed class WalletContext
    {
        /// <summary>Game instance that caused the movement, if any.</summary>
        public Guid? GameId { get; set; }

        /// <summary>Table the movement originated from, if any.</summary>
        public string TableId { get; set; }

        /// <summary>Round/hand the movement belongs to, if any.</summary>
        public string RoundId { get; set; }

        /// <summary>External reference, e.g. an IAP receipt id or a game-hand id.</summary>
        public string ExternalRef { get; set; }

        /// <summary>Human-readable note, e.g. "Blackjack win, hand 0".</summary>
        public string Description { get; set; }

        /// <summary>Free-form JSON for additional structured metadata.</summary>
        public string MetadataJson { get; set; }

        /// <summary>
        /// For a CREDIT only: how much of the credited amount is TAINTED (added to the wallet's gifted
        /// portion); the remainder is clean/earned. Null/0 = the whole credit is earned — the default for
        /// winnings, IAP, house bonuses and level rewards. Set to the full amount for a player-to-player gift
        /// claim, or to <c>gross × giftedStakeRatio</c> for a bet payout so a win keeps the stake's gifted
        /// fraction and can't launder gifted chips clean. Ignored for debits (which always spend earned first).
        /// </summary>
        public decimal? CreditGiftedAmount { get; set; }
    }

    /// <summary>
    /// Thrown when a debit would push a wallet balance below zero. Derives from
    /// <see cref="InvalidOperationException"/> so existing controller catch blocks
    /// surface it as a clean 400 with a message.
    /// </summary>
    public sealed class InsufficientFundsException : InvalidOperationException
    {
        public Guid WalletId { get; }
        public CurrencyType Currency { get; }
        public decimal Balance { get; }
        public decimal Requested { get; }

        public InsufficientFundsException(Guid walletId, CurrencyType currency, decimal balance, decimal requested)
            : base($"Insufficient {currency} funds in wallet {walletId}: balance {balance}, requested {requested}.")
        {
            WalletId = walletId;
            Currency = currency;
            Balance = balance;
            Requested = requested;
        }
    }

    /// <summary>Thrown when an operation targets a wallet flagged as locked.</summary>
    public sealed class WalletLockedException : InvalidOperationException
    {
        public Guid WalletId { get; }

        public WalletLockedException(Guid walletId)
            : base($"Wallet {walletId} is locked and cannot be modified.")
        {
            WalletId = walletId;
        }
    }
}
