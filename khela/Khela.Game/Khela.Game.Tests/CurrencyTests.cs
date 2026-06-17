using Khela.Game.Database.Models;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// CurrencyType is persisted as an <c>int</c> in PlayerWallet.Currency, so its values must be
    /// APPEND-ONLY — reordering or inserting would silently renumber existing wallet rows. This pins
    /// the numeric values so a careless reorder fails the test gate. Kash (cosmetics/gifting spend)
    /// is the newest value, appended at the end.
    /// </summary>
    public class CurrencyTests
    {
        [Fact]
        public void CurrencyType_NumericValues_AreStable()
        {
            Assert.Equal(0, (int)CurrencyType.Chips);
            Assert.Equal(1, (int)CurrencyType.Coins);
            Assert.Equal(2, (int)CurrencyType.Gems);
            Assert.Equal(3, (int)CurrencyType.Tokens);
            Assert.Equal(4, (int)CurrencyType.Kash);
        }
    }
}
