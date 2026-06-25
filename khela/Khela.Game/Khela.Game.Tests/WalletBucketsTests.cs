using Khela.Game.Services.Wallet;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the clean-vs-tainted bucket arithmetic (Progression Spec §6): bets spend EARNED first, gifts
    /// land fully tainted, and a winning payout credited with gross × giftedStakeRatio keeps the stake's
    /// gifted fraction so gifted chips can never be laundered clean by winning. Pure math — no DB needed.
    /// </summary>
    public class WalletBucketsTests
    {
        // ---- Debit: spend earned first ----

        [Fact]
        public void Debit_WithinEarned_LeavesGiftedUntouched()
        {
            // Balance 1000, of which 300 gifted → 700 earned. Bet 500 fits in earned.
            Assert.Equal(0m, WalletBuckets.GiftedDelta(balance: 1000m, giftedBalance: 300m, signedAmount: -500m, creditGiftedHint: null));
        }

        [Fact]
        public void Debit_AtEarnedBoundary_LeavesGiftedUntouched()
        {
            // 700 earned, bet exactly 700 → gifted still untouched.
            Assert.Equal(0m, WalletBuckets.GiftedDelta(1000m, 300m, -700m, null));
        }

        [Fact]
        public void Debit_OverrunsEarned_DipsIntoGiftedByOverrunOnly()
        {
            // 700 earned, bet 900 → 200 overrun comes out of gifted.
            Assert.Equal(-200m, WalletBuckets.GiftedDelta(1000m, 300m, -900m, null));
        }

        [Fact]
        public void Debit_WholeBalanceGifted_SpendsGiftedOneForOne()
        {
            // Balance 1000, all gifted → every chip spent is gifted.
            Assert.Equal(-400m, WalletBuckets.GiftedDelta(1000m, 1000m, -400m, null));
        }

        // ---- Credit: clean by default, tainted only when pinned ----

        [Fact]
        public void Credit_NoHint_IsFullyEarned()
        {
            // Winnings / IAP / house bonus: no hint → nothing added to gifted.
            Assert.Equal(0m, WalletBuckets.GiftedDelta(0m, 0m, 2000m, creditGiftedHint: null));
        }

        [Fact]
        public void Credit_PlayerGift_IsFullyTainted()
        {
            // A player-to-player gift pins the whole amount as gifted.
            Assert.Equal(1000m, WalletBuckets.GiftedDelta(0m, 0m, 1000m, creditGiftedHint: 1000m));
        }

        [Fact]
        public void Credit_ProportionalPayout_TaintsTheStakesGiftedFraction()
        {
            // Win 2000 on a stake that was 100% gifted → the whole payout stays gifted (no launder).
            Assert.Equal(2000m, WalletBuckets.GiftedDelta(0m, 0m, 2000m, creditGiftedHint: 2000m));
            // Win 2000 on a 70/30 earned/gifted stake → 30% (600) of the payout is gifted.
            Assert.Equal(600m, WalletBuckets.GiftedDelta(0m, 0m, 2000m, creditGiftedHint: 600m));
        }

        [Fact]
        public void Credit_HintIsClampedToTheCreditAmount()
        {
            // A slightly-rounded-high gross × ratio can never tag more than the credit itself...
            Assert.Equal(500m, WalletBuckets.GiftedDelta(0m, 0m, 500m, creditGiftedHint: 999m));
            // ...nor can a negative hint pull the gifted slice down.
            Assert.Equal(0m, WalletBuckets.GiftedDelta(0m, 0m, 500m, creditGiftedHint: -10m));
        }

        // ---- The laundering scenario the whole feature exists to stop ----

        [Fact]
        public void GiftedChips_StayTainted_ThroughAWin()
        {
            // Start: 1000 balance, all gifted (claimed a gift, no earned chips).
            decimal balance = 1000m, gifted = 1000m;

            // Bet the whole 1000. Earned-first draw spends gifted (none earned) → gifted drops to 0.
            var betDelta = WalletBuckets.GiftedDelta(balance, gifted, -1000m, null);
            balance -= 1000m; gifted += betDelta;
            Assert.Equal(0m, balance);
            Assert.Equal(0m, gifted);

            // Stake was 100% gifted, so the payout credits back 100% gifted (giftedStakeRatio = 1).
            var win = 2000m;
            var winDelta = WalletBuckets.GiftedDelta(balance, gifted, win, creditGiftedHint: win * 1.0m);
            balance += win; gifted += winDelta;

            // The winnings are STILL fully gifted — nothing was laundered into the earned bucket.
            Assert.Equal(2000m, balance);
            Assert.Equal(2000m, gifted);
            Assert.Equal(0m, balance - gifted); // earned balance == 0
        }
    }
}
