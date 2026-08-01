using Khela.Game.Services.Wallet;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the derivation of a ROLLBACK's correlation id. This is the whole basis of rollback idempotency: the
    /// reversal's id is derived from the original's, so a repeated rollback collides with the unique
    /// (WalletId, CorrelationId) index and returns the existing reversal instead of refunding twice. If this
    /// derivation ever stopped being deterministic — or started colliding across different originals — the wallet
    /// would silently double-refund, which under a real-money licence is the worst class of bug there is.
    ///
    /// NOTE on coverage: the ledger behaviour itself (row lock, balance write, marking the original Reversed) needs
    /// a real MySQL harness — WalletService uses `SELECT ... FOR UPDATE` and an explicit transaction, neither of
    /// which the in-memory provider honours. These tests cover the pure part; the rest is integration-tested work
    /// still outstanding.
    /// </summary>
    public class WalletRollbackIdTests
    {
        [Fact]
        public void ShortId_IsPrefixed_AndReadable()
        {
            Assert.Equal("rb:bjr:r1:s2:stk", WalletService.ReversalIdFor("bjr:r1:s2:stk"));
        }

        [Fact]
        public void IsDeterministic_SoARepeatedRollbackHitsTheSameRow()
        {
            const string original = "bjr:round-abc:seat3:stk";
            Assert.Equal(WalletService.ReversalIdFor(original), WalletService.ReversalIdFor(original));
        }

        [Fact]
        public void LongId_StillFitsTheColumn()
        {
            // The CorrelationId column is 64 chars; a long game id must not be truncated into a collision.
            var longId = new string('x', 200);
            var reversal = WalletService.ReversalIdFor(longId);
            Assert.True(reversal.Length <= 64, $"reversal id was {reversal.Length} chars");
        }

        [Fact]
        public void LongIds_ThatShareAPrefix_DoNotCollide()
        {
            // Truncating instead of hashing would map both of these to the same reversal id — and the second
            // rollback would then return the FIRST one's reversal and silently reverse nothing.
            var a = new string('x', 100) + "-seat1";
            var b = new string('x', 100) + "-seat2";
            Assert.NotEqual(WalletService.ReversalIdFor(a), WalletService.ReversalIdFor(b));
        }

        [Fact]
        public void ReversalOfAReversal_IsNotTheOriginal()
        {
            // Rolling back a rollback must not resolve back to the original id, or a double rollback would look
            // idempotent while actually re-applying the original movement.
            const string original = "bjr:r9:s1:pay";
            var once = WalletService.ReversalIdFor(original);
            Assert.NotEqual(original, WalletService.ReversalIdFor(once));
        }
    }
}
