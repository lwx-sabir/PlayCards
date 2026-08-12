using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Khela.Game.Services.Pass
{
    /// <summary>What an ad-intent token says, once it has been verified.</summary>
    public sealed class PassAdIntent
    {
        public Guid UserId { get; set; }
        public string PassKey { get; set; }
        public string CycleKey { get; set; }
        public int Node { get; set; }
        public string Nonce { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    /// <summary>
    /// The single-use token that ties a rewarded-ad view to ONE player, pass, cycle and day.
    ///
    /// The client asks for a token, hands it to the ad SDK as custom data, and the ad network echoes it back to us in
    /// its server-to-server callback. Because the token is HMAC-signed with a server secret, the callback can't be
    /// forged into crediting a different player or day — even though the value itself makes a round trip through an
    /// untrusted client and a third party.
    ///
    /// PURE and unit-tested. The token is NOT a credit: the callback still has to carry a valid network signature
    /// (<c>IAdSsvVerifier</c>) and a fresh transaction id before anything is written.
    /// </summary>
    public static class PassAdTokens
    {
        private const string Version = "v1";

        /// <summary>How long a token stays usable. Long enough to watch an ad and for the callback to arrive, short
        /// enough that a leaked token is worthless.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

        /// <summary>Issue a token for (player, pass, cycle, day). <paramref name="nonce"/> is supplied so this stays
        /// pure and testable; callers pass a fresh GUID.</summary>
        public static string Issue(Guid userId, string passKey, string cycleKey, int node, string secret,
            DateTime nowUtc, string nonce)
        {
            var body = string.Join('.', Version, userId.ToString("N"), passKey, cycleKey,
                node.ToString(CultureInfo.InvariantCulture),
                ToUnix(nowUtc.Add(Lifetime)).ToString(CultureInfo.InvariantCulture),
                nonce);
            return body + "." + Sign(body, secret);
        }

        /// <summary>
        /// Verify and unpack a token. Returns null (with a reason) for anything tampered with, expired, or signed
        /// with a different secret — never throws on hostile input, since this is fed straight from a public callback.
        /// </summary>
        public static PassAdIntent Verify(string token, string secret, DateTime nowUtc, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(token)) { error = "Missing token."; return null; }

            var parts = token.Split('.');
            if (parts.Length != 8 || parts[0] != Version) { error = "Malformed token."; return null; }

            var body = string.Join('.', parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], parts[6]);
            var expected = Sign(body, secret);
            // Fixed-time compare: a byte-by-byte early exit would leak the signature one character at a time.
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[7])))
            { error = "Bad token signature."; return null; }

            if (!Guid.TryParseExact(parts[1], "N", out var userId)) { error = "Bad token user."; return null; }
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var node)) { error = "Bad token node."; return null; }
            if (!long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp)) { error = "Bad token expiry."; return null; }

            var expiresUtc = FromUnix(exp);
            if (expiresUtc <= nowUtc) { error = "Token expired."; return null; }

            return new PassAdIntent
            {
                UserId = userId,
                PassKey = parts[2],
                CycleKey = parts[3],
                Node = node,
                Nonce = parts[6],
                ExpiresUtc = expiresUtc,
            };
        }

        private static string Sign(string body, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? string.Empty));
            return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
        }

        private static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static long ToUnix(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalSeconds;
        private static DateTime FromUnix(long seconds) => DateTime.UnixEpoch.AddSeconds(seconds);
    }
}
