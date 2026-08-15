using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Khela.Game.Games.VideoPoker
{
    /// <summary>
    /// Tamper-evidence + result-integrity hashes for the video-poker ledger. Both are deterministic SHA-256 over a
    /// canonical string, so the verify endpoint (and any third party) recomputes them from the stored fields alone.
    /// A hand's <see cref="HandHash"/> folds in the PREVIOUS hand's hash, chaining a player's hands: editing any
    /// settled row changes its hash and breaks the next hand's recorded <c>PrevHandHash</c> link.
    /// </summary>
    public static class VideoPokerLedger
    {
        /// <summary>Integrity hash of the settled outcome — the final hand, its category, and the coins paid.</summary>
        public static string ResultChecksum(IEnumerable<string> finalCanonical, string category, int payoutCoins)
            => Sha256Hex($"{string.Join(",", finalCanonical)}|{category}|{payoutCoins}");

        /// <summary>This hand's chain hash: its immutable settlement fields + the previous hand's hash. Recomputable
        /// from the audit row alone, so a verifier can walk the chain (each hand's hash == the next hand's PrevHandHash).</summary>
        public static string HandHash(string handId, string serverSeedHash, string deckHash, string resultChecksum, decimal bet, decimal payout, string prevHandHash)
            => Sha256Hex($"{handId}|{serverSeedHash}|{deckHash}|{resultChecksum}|{Money(bet)}|{Money(payout)}|{prevHandHash ?? string.Empty}");

        private static string Money(decimal d) => d.ToString("0.0000", CultureInfo.InvariantCulture);

        private static string Sha256Hex(string s)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }
}
