using System;

namespace Khela.Game.Services.Wallet
{
    /// <summary>
    /// Pure arithmetic for the clean-vs-tainted wallet split (Progression Spec §6), factored out of
    /// <see cref="WalletService"/> so it can be unit-tested without a database, row lock, or transaction.
    /// The wallet stores a single <c>GiftedBalance</c> (the tainted slice of <c>Balance</c>); the earned
    /// balance is the remainder, <c>Balance - GiftedBalance</c>.
    /// </summary>
    public static class WalletBuckets
    {
        /// <summary>
        /// The signed change to apply to the wallet's GIFTED slice for a movement of
        /// <paramref name="signedAmount"/> (negative = debit, positive = credit) on a wallet currently at
        /// (<paramref name="balance"/>, <paramref name="giftedBalance"/>).
        /// <list type="bullet">
        /// <item>Debit — spends EARNED first; only dips into gifted for the overrun once earned is exhausted.</item>
        /// <item>Credit — clean by default (returns 0); <paramref name="creditGiftedHint"/> pins a tainted
        /// portion (clamped to <c>[0, amount]</c>), e.g. the full amount for a player gift or
        /// <c>gross × giftedStakeRatio</c> for a bet payout.</item>
        /// </list>
        /// Guarantees the invariant <c>0 ≤ giftedBalance + result ≤ balance + signedAmount</c> holds for any
        /// movement the wallet itself permits (i.e. one that doesn't overdraw the total balance).
        /// </summary>
        public static decimal GiftedDelta(decimal balance, decimal giftedBalance, decimal signedAmount, decimal? creditGiftedHint)
        {
            if (signedAmount < 0m)
            {
                var magnitude = -signedAmount;
                var earnedAvailable = balance - giftedBalance;          // = clean chips available
                return -Math.Max(0m, magnitude - earnedAvailable);      // gifted spent only after earned
            }

            // Credit: clean unless the caller pins a tainted slice. Clamp so a credit can never tag more as
            // gifted than the credit itself (de-risks a slightly-rounded-high gross × ratio from C).
            return creditGiftedHint is decimal taint ? Math.Clamp(taint, 0m, signedAmount) : 0m;
        }
    }
}
