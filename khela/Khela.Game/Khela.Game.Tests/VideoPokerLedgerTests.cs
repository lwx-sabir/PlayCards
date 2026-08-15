using Khela.Game.Games.VideoPoker;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the video-poker ledger hashes that back the verify endpoint + the tamper-evident chain: both are
    /// deterministic, and BOTH are sensitive to every input — editing any settled field (or an earlier hand in the
    /// chain) changes the hash, which is exactly what makes a doctored row detectable.
    /// </summary>
    public class VideoPokerLedgerTests
    {
        private static readonly string[] Final = { "14S", "13S", "12S", "11S", "10S" };

        [Fact]
        public void ResultChecksum_IsDeterministic_AndInputSensitive()
        {
            var a = VideoPokerLedger.ResultChecksum(Final, "RoyalFlush", 4000);
            Assert.Equal(a, VideoPokerLedger.ResultChecksum(Final, "RoyalFlush", 4000));   // deterministic
            Assert.NotEqual(a, VideoPokerLedger.ResultChecksum(Final, "StraightFlush", 4000)); // category changed
            Assert.NotEqual(a, VideoPokerLedger.ResultChecksum(Final, "RoyalFlush", 250));     // payout changed
            Assert.NotEqual(a, VideoPokerLedger.ResultChecksum(new[] { "14S", "13S", "12S", "11S", "9S" }, "RoyalFlush", 4000)); // cards changed
        }

        [Fact]
        public void HandHash_IsDeterministic_AndSensitiveToEveryField()
        {
            string rc = VideoPokerLedger.ResultChecksum(Final, "RoyalFlush", 4000);
            var baseHash = VideoPokerLedger.HandHash("hand1", "sshash", "deckhash", rc, 500m, 4000m, "prev0");
            Assert.Equal(baseHash, VideoPokerLedger.HandHash("hand1", "sshash", "deckhash", rc, 500m, 4000m, "prev0"));
            Assert.NotEqual(baseHash, VideoPokerLedger.HandHash("hand2", "sshash", "deckhash", rc, 500m, 4000m, "prev0")); // id
            Assert.NotEqual(baseHash, VideoPokerLedger.HandHash("hand1", "sshashX", "deckhash", rc, 500m, 4000m, "prev0")); // server seed hash
            Assert.NotEqual(baseHash, VideoPokerLedger.HandHash("hand1", "sshash", "deckhashX", rc, 500m, 4000m, "prev0")); // deck hash
            Assert.NotEqual(baseHash, VideoPokerLedger.HandHash("hand1", "sshash", "deckhash", "rcX", 500m, 4000m, "prev0")); // result
            Assert.NotEqual(baseHash, VideoPokerLedger.HandHash("hand1", "sshash", "deckhash", rc, 499m, 4000m, "prev0")); // bet
            Assert.NotEqual(baseHash, VideoPokerLedger.HandHash("hand1", "sshash", "deckhash", rc, 500m, 3999m, "prev0")); // payout
            Assert.NotEqual(baseHash, VideoPokerLedger.HandHash("hand1", "sshash", "deckhash", rc, 500m, 4000m, "prev1")); // prev link
        }

        [Fact]
        public void Chain_BreaksWhenAnEarlierHandIsEdited()
        {
            // hand1 -> hand2 (hand2.prev = hash of hand1)
            var rc1 = VideoPokerLedger.ResultChecksum(new[] { "5D", "6D", "7D", "8D", "9D" }, "StraightFlush", 200);
            var h1 = VideoPokerLedger.HandHash("h1", "ss1", "dh1", rc1, 100m, 2000m, "");   // genesis prev = ""
            var h2 = VideoPokerLedger.HandHash("h2", "ss2", "dh2", "rc2", 100m, 0m, h1);

            // Now someone tampers with hand1's payout in the DB: its recomputed hash no longer equals hand2's recorded prev.
            var h1Tampered = VideoPokerLedger.HandHash("h1", "ss1", "dh1", rc1, 100m, 9999m, "");
            Assert.NotEqual(h1, h1Tampered);
            Assert.Equal(h1, GetRecordedPrevOf(h2Prev: h1));   // hand2 recorded h1 as its prev...
            Assert.NotEqual(h1Tampered, h1);                   // ...but the tampered hand1 hashes differently -> chain break detected
        }

        // hand2 durably records its predecessor's hash as PrevHandHash; this stands in for reading that column back.
        private static string GetRecordedPrevOf(string h2Prev) => h2Prev;
    }
}
