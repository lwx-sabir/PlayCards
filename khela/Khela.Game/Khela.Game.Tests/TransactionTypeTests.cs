using Khela.Game.Database.Models;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// TransactionType is persisted as an <c>int</c> in WalletTransactions.Type, so its values are APPEND-ONLY. This pins
    /// them — and pins that real-money store credits are <see cref="TransactionType.PaidPurchase"/> (6), distinct from
    /// <see cref="TransactionType.Purchase"/> (2), which is an in-game spend (the cosmetics debit). A revenue query that
    /// sums <c>Purchase</c> is wrong; one that sums <c>PaidPurchase</c> is right. See docs/IAP_SPEC.md §4.4.
    /// </summary>
    public class TransactionTypeTests
    {
        [Fact]
        public void TransactionType_NumericValues_AreStable()
        {
            Assert.Equal(0, (int)TransactionType.Bet);
            Assert.Equal(1, (int)TransactionType.Win);
            Assert.Equal(2, (int)TransactionType.Purchase);
            Assert.Equal(3, (int)TransactionType.Refund);
            Assert.Equal(4, (int)TransactionType.Bonus);
            Assert.Equal(5, (int)TransactionType.AdminAdjustment);
            Assert.Equal(6, (int)TransactionType.PaidPurchase);
        }

        [Fact]
        public void PaidPurchase_IsNotPurchase()
        {
            Assert.NotEqual(TransactionType.Purchase, TransactionType.PaidPurchase);
        }
    }
}
