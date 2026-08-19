using System.Security.Cryptography;
using Khela.Game.Database;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Services.Identity
{
    /// <summary>
    /// The player-facing PUBLIC ID ("Player ID"): an 8-char, case-insensitive alphanumeric code assigned ONCE at
    /// profile creation and NEVER changed. It's the stable handle players use to find each other (distinct from the
    /// internal <c>UserId</c> GUID). Stored UPPERCASE so 'A' == 'a' regardless of DB collation; the unique index on
    /// <c>UserProfiles.PublicId</c> enforces global uniqueness. Every lookup must <see cref="Normalize"/> its input.
    /// </summary>
    public static class PublicPlayerId
    {
        // 8 chars from A-Z0-9 (36^8 ≈ 2.8e12 codes). It is a human-shareable code, so to drop the easily-confused
        // look-alikes (0/O and 1/I) switch this to "23456789ABCDEFGHJKLMNPQRSTUVWXYZ" — one-line change, still 8 chars.
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        public const int Length = 8;

        /// <summary>One random candidate — crypto-strong and unbiased (RandomNumberGenerator.GetInt32). Not yet
        /// checked for uniqueness; use <see cref="AllocateAsync"/> to get a code guaranteed free in the DB.</summary>
        public static string Generate()
        {
            var chars = new char[Length];
            for (int i = 0; i < Length; i++)
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            return new string(chars);
        }

        /// <summary>Normalize user-typed input for a case-insensitive lookup (trim + uppercase). Null/blank -> null.</summary>
        public static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            return input.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Allocate a globally-unique code: generate a candidate and pre-check it against the DB, retrying on the
        /// (astronomically rare) collision. The unique index is the ultimate backstop should a pre-check lose a race.
        /// Call this BEFORE Add-ing a new profile. Optionally pass <paramref name="reservedLocally"/> to also avoid
        /// codes handed out earlier in the same batch (e.g. a backfill) that aren't saved yet.
        /// </summary>
        public static async Task<string> AllocateAsync(AppDbContext db, ISet<string> reservedLocally = null, CancellationToken ct = default)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                var code = Generate();
                if (reservedLocally != null && reservedLocally.Contains(code)) continue;
                if (!await db.UserProfiles.AsNoTracking().AnyAsync(p => p.PublicId == code, ct))
                {
                    reservedLocally?.Add(code);
                    return code;
                }
            }
            // 12 straight collisions is impossible until the table nears the whole keyspace — fail loud, don't loop.
            throw new InvalidOperationException("Could not allocate a unique Player ID after 12 attempts.");
        }
    }
}
