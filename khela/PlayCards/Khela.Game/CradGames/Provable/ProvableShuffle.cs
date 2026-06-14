using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CardGames.Platforms;

namespace CardGames.Provable
{
    /// <summary>
    /// Provably-fair shuffle utilities. The shuffle is a PURE FUNCTION of a seed, so any hand can be
    /// independently recomputed and checked.
    ///
    /// Seed lifecycle (filled in by the table layer in a later pass):
    ///   • a secret per-session <c>serverSeed</c> is committed up front via SHA-256(serverSeed),
    ///   • combined with a <c>clientSeed</c> and a per-hand <c>nonce</c> through <see cref="DeriveSeed"/>,
    ///   • the result seeds <see cref="Shuffle"/>,
    ///   • the serverSeed is revealed at rotation so anyone can replay every hand under it.
    /// </summary>
    public static class ProvableShuffle
    {
        /// <summary>
        /// Per-hand seed = HMAC-SHA256(serverSeed, "clientSeed:nonce"). Unpredictable until the
        /// serverSeed is revealed; reproducible the instant it is.
        /// </summary>
        public static byte[] DeriveSeed(byte[] serverSeed, string clientSeed, long nonce)
        {
            if (serverSeed == null || serverSeed.Length == 0)
                throw new ArgumentException("serverSeed must be non-empty.", nameof(serverSeed));

            using var hmac = new HMACSHA256(serverSeed);
            var message = Encoding.UTF8.GetBytes($"{clientSeed ?? string.Empty}:{nonce}");
            return hmac.ComputeHash(message);
        }

        /// <summary>
        /// In-place Fisher–Yates driven by <see cref="DeterministicRng"/>. Same seed → same order,
        /// every time, on every machine.
        /// </summary>
        public static void Shuffle(IList<Card> cards, byte[] seed)
        {
            if (cards == null) throw new ArgumentNullException(nameof(cards));
            var rng = new DeterministicRng(seed);
            for (int i = cards.Count - 1; i > 0; i--)
            {
                int j = rng.NextIndex(i + 1);
                (cards[i], cards[j]) = (cards[j], cards[i]);
            }
        }

        /// <summary>
        /// Stable fingerprint of an ordered deck: SHA-256 over "RANKSUIT,RANKSUIT,…" where RANK is
        /// the numeric face value (2–14) and SUIT is one of D/S/C/H. Confirms a stored deck matches
        /// its seed, and makes any after-the-fact edit detectable.
        /// </summary>
        public static string DeckHash(IEnumerable<Card> cards)
        {
            if (cards == null) throw new ArgumentNullException(nameof(cards));
            var canonical = string.Join(",", cards.Select(Canonical));
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>Canonical single-card token, e.g. Ace of Hearts → "14H", Two of Diamonds → "2D".</summary>
        public static string Canonical(Card c) => $"{(int)c.FaceVal}{SuitChar(c.Suit)}";

        private static char SuitChar(Suit s) => s switch
        {
            Suit.Diamonds => 'D',
            Suit.Spades => 'S',
            Suit.Clubs => 'C',
            Suit.Hearts => 'H',
            _ => '?'
        };
    }
}
