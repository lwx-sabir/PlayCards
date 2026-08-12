using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Ads
{
    /// <summary>A rewarded-ad server-side-verification callback, as the network sent it.</summary>
    public sealed class AdSsvCallback
    {
        /// <summary>The raw query string WITHOUT the leading '?' — signature verification is over the exact bytes the
        /// network signed, so it can never be rebuilt from a parsed dictionary.</summary>
        public string RawQuery { get; set; }

        public IReadOnlyDictionary<string, string> Query { get; set; }

        public string TransactionId => Get("transaction_id");
        public string CustomData => Get("custom_data");
        public string UserId => Get("user_id");

        public string Get(string name) => Query != null && Query.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>
    /// Verifies that a rewarded-ad callback really came from the ad network. This is the ONLY thing standing between
    /// "a player watched an ad" and "a player says they watched an ad", so a verifier that can't check a signature
    /// must fail CLOSED.
    ///
    /// One implementation per network; the active one is chosen by <c>Ads:Provider</c>.
    /// </summary>
    public interface IAdSsvVerifier
    {
        string Provider { get; }
        Task<(bool Ok, string Error)> VerifyAsync(AdSsvCallback callback, CancellationToken ct = default);
    }

    /// <summary>
    /// Refuses everything. The registered verifier when <c>Ads:Provider</c> is unset or unknown, so a deployment that
    /// forgets to configure ads grants no credits rather than granting them to anyone who asks.
    /// </summary>
    public sealed class DisabledAdSsvVerifier : IAdSsvVerifier
    {
        private readonly ILogger<DisabledAdSsvVerifier> _logger;
        public DisabledAdSsvVerifier(ILogger<DisabledAdSsvVerifier> logger) => _logger = logger;

        public string Provider => "disabled";

        public Task<(bool, string)> VerifyAsync(AdSsvCallback callback, CancellationToken ct = default)
        {
            _logger.LogWarning("Ad SSV callback refused: no ad provider configured (set Ads:Provider).");
            return Task.FromResult((false, "Ad verification is not configured."));
        }
    }

    /// <summary>
    /// HMAC-SHA256 over the signed portion of the query with a shared secret — the scheme Unity Ads and ironSource
    /// style callbacks use. Set <c>Ads:Provider=hmac</c> and <c>Ads:Secret</c>.
    /// </summary>
    public sealed class HmacAdSsvVerifier : IAdSsvVerifier
    {
        private readonly string _secret;
        private readonly string _signatureParam;

        public HmacAdSsvVerifier(IConfiguration config)
        {
            _secret = config.GetValue<string>("Ads:Secret");
            _signatureParam = config.GetValue("Ads:SignatureParam", "signature");
        }

        public string Provider => "hmac";

        public Task<(bool, string)> VerifyAsync(AdSsvCallback callback, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_secret)) return Task.FromResult((false, "Ads:Secret is not set."));

            var provided = callback.Get(_signatureParam);
            if (string.IsNullOrWhiteSpace(provided)) return Task.FromResult((false, "Callback has no signature."));

            var signed = AdSsvSigning.SignedPortion(callback.RawQuery, _signatureParam);
            if (signed == null) return Task.FromResult((false, "Callback signature is not the last parameter."));

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();

            var ok = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(hex), Encoding.UTF8.GetBytes(provided.Trim().ToLowerInvariant()));
            return Task.FromResult(ok ? (true, (string)null) : (false, "Callback signature did not verify."));
        }
    }

    /// <summary>
    /// Google AdMob SSV: ECDSA-SHA256 over the query up to (not including) <c>&amp;signature=</c>, using the public key
    /// whose <c>key_id</c> the callback names, fetched from Google's published key set and cached.
    /// Set <c>Ads:Provider=admob</c>.
    /// </summary>
    public sealed class AdMobSsvVerifier : IAdSsvVerifier
    {
        private const string KeyUrl = "https://www.gstatic.com/admob/reward/verifier-keys.json";
        private static readonly TimeSpan KeyCacheTtl = TimeSpan.FromHours(24);

        private readonly IHttpClientFactory _http;
        private readonly ILogger<AdMobSsvVerifier> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private Dictionary<string, string> _keys;     // key_id → PEM
        private DateTime _fetchedUtc;

        public AdMobSsvVerifier(IHttpClientFactory http, ILogger<AdMobSsvVerifier> logger)
        {
            _http = http; _logger = logger;
        }

        public string Provider => "admob";

        public async Task<(bool, string)> VerifyAsync(AdSsvCallback callback, CancellationToken ct = default)
        {
            var signature = callback.Get("signature");
            var keyId = callback.Get("key_id");
            if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(keyId))
                return (false, "Callback is missing signature or key_id.");

            // AdMob signs everything BEFORE "&signature=" — the two trailing parameters are excluded by definition.
            var signed = AdSsvSigning.SignedPortion(callback.RawQuery, "signature");
            if (signed == null) return (false, "Callback signature is not positioned as AdMob sends it.");

            var pem = await KeyAsync(keyId, ct);
            if (pem == null) return (false, $"Unknown AdMob key_id '{keyId}'.");

            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(pem);
                var ok = ecdsa.VerifyData(Encoding.UTF8.GetBytes(signed), AdSsvSigning.FromBase64Url(signature),
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                return ok ? (true, (string)null) : (false, "Callback signature did not verify.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AdMob SSV verification threw");
                return (false, "Callback signature could not be checked.");
            }
        }

        private async Task<string> KeyAsync(string keyId, CancellationToken ct)
        {
            if (_keys != null && _keys.TryGetValue(keyId, out var cached) && DateTime.UtcNow - _fetchedUtc < KeyCacheTtl)
                return cached;

            await _lock.WaitAsync(ct);
            try
            {
                if (_keys == null || DateTime.UtcNow - _fetchedUtc >= KeyCacheTtl || !_keys.ContainsKey(keyId))
                {
                    // A key_id we've never seen means Google rotated keys — refetch rather than reject.
                    var client = _http.CreateClient(nameof(AdMobSsvVerifier));
                    var json = await client.GetStringAsync(KeyUrl, ct);
                    _keys = ParseKeys(json);
                    _fetchedUtc = DateTime.UtcNow;
                }
                return _keys != null && _keys.TryGetValue(keyId, out var pem) ? pem : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not fetch AdMob verifier keys");
                return null;   // fail closed
            }
            finally { _lock.Release(); }
        }

        /// <summary>Parse Google's <c>{ "keys": [ { "keyId": 1234, "pem": "-----BEGIN PUBLIC KEY-----…" } ] }</c>.</summary>
        public static Dictionary<string, string> ParseKeys(string json)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("keys", out var keys)) return map;
            foreach (var k in keys.EnumerateArray())
            {
                if (!k.TryGetProperty("keyId", out var id) || !k.TryGetProperty("pem", out var pem)) continue;
                map[id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString()] = pem.GetString();
            }
            return map;
        }
    }

    /// <summary>The parts of SSV verification that are pure string work — split out so they're unit-testable without
    /// crypto, a network, or a live callback.</summary>
    public static class AdSsvSigning
    {
        /// <summary>
        /// The exact substring a network signed: everything from the start of the query up to (not including)
        /// <c>&amp;{signatureParam}=</c>. Returns null when the signature isn't present as a trailing parameter —
        /// which is itself a reason to refuse, since a callback with the signature moved is not one we can check.
        /// </summary>
        public static string SignedPortion(string rawQuery, string signatureParam = "signature")
        {
            if (string.IsNullOrEmpty(rawQuery)) return null;
            var query = rawQuery.StartsWith("?", StringComparison.Ordinal) ? rawQuery.Substring(1) : rawQuery;

            var marker = "&" + signatureParam + "=";
            var i = query.IndexOf(marker, StringComparison.Ordinal);
            if (i <= 0) return null;             // absent, or the very first parameter (nothing would be signed)
            return query.Substring(0, i);
        }

        /// <summary>Decode base64url (no padding), the encoding ad networks use for signatures.</summary>
        public static byte[] FromBase64Url(string value)
        {
            var s = value.Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        /// <summary>Split a raw query into a dictionary WITHOUT losing the raw form (which is what gets verified).</summary>
        public static Dictionary<string, string> ParseQuery(string rawQuery)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(rawQuery)) return map;
            var query = rawQuery.StartsWith("?", StringComparison.Ordinal) ? rawQuery.Substring(1) : rawQuery;

            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var name = eq < 0 ? pair : pair.Substring(0, eq);
                var value = eq < 0 ? string.Empty : pair.Substring(eq + 1);
                map[Uri.UnescapeDataString(name)] = Uri.UnescapeDataString(value);
            }
            return map;
        }
    }
}
