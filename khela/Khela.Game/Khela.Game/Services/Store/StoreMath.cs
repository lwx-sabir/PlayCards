using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Khela.Common.Store;

namespace Khela.Game.Services.Store
{
    /// <summary>
    /// Pure, DB-free helpers for the purchase spine: receipt parsing, the idempotency keys (all ≤ 64 chars — the wallet's
    /// CorrelationId budget), hashes and clamps. Unit-tested in StoreMathTests; nothing here does I/O.
    /// </summary>
    public static class StoreMath
    {
        /// <summary>Unity IAP's unified receipt: <c>{"Store":"GooglePlay","TransactionID":"…","Payload":"…"}</c>.</summary>
        public sealed class UnifiedReceipt
        {
            public string Store { get; set; }
            public string TransactionId { get; set; }
            public string Payload { get; set; }
        }

        /// <summary>Google Play's payload inside the unified receipt: <c>{"json":"{…}","signature":"…","skuDetails":[…]}</c>, with the
        /// INAPP_PURCHASE_DATA fields lifted out of <c>json</c>.</summary>
        public sealed class GooglePayload
        {
            public string Json { get; set; }
            public string Signature { get; set; }
            public string PurchaseToken { get; set; }
            public string ProductId { get; set; }
            public string PackageName { get; set; }
            public string OrderId { get; set; }
            public int? PurchaseState { get; set; }
            public string ObfuscatedAccountId { get; set; }
            public int? Quantity { get; set; }
            public long? PurchaseTimeMillis { get; set; }
            public bool? Acknowledged { get; set; }
            public bool? AutoRenewing { get; set; }
        }

        private static readonly JsonSerializerOptions Lenient = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>Parse the unified receipt; null when it isn't one.</summary>
        public static UnifiedReceipt ParseUnifiedReceipt(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                var r = new UnifiedReceipt
                {
                    Store = GetString(doc.RootElement, "Store") ?? GetString(doc.RootElement, "store"),
                    TransactionId = GetString(doc.RootElement, "TransactionID") ?? GetString(doc.RootElement, "transactionID") ?? GetString(doc.RootElement, "TransactionId"),
                    Payload = GetString(doc.RootElement, "Payload") ?? GetString(doc.RootElement, "payload"),
                };
                return string.IsNullOrWhiteSpace(r.Store) ? null : r;
            }
            catch { return null; }
        }

        /// <summary>Parse the Google Play payload (the unified receipt's <c>Payload</c>); null when it isn't one.</summary>
        public static GooglePayload ParseGooglePayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                using var outer = JsonDocument.Parse(payload);
                if (outer.RootElement.ValueKind != JsonValueKind.Object) return null;
                var g = new GooglePayload
                {
                    Json = GetString(outer.RootElement, "json"),
                    Signature = GetString(outer.RootElement, "signature"),
                };
                if (string.IsNullOrWhiteSpace(g.Json)) return null;
                using var inner = JsonDocument.Parse(g.Json);
                var e = inner.RootElement;
                if (e.ValueKind != JsonValueKind.Object) return null;
                g.PurchaseToken = GetString(e, "purchaseToken");
                g.ProductId = GetString(e, "productId");
                g.PackageName = GetString(e, "packageName");
                g.OrderId = GetString(e, "orderId");
                g.ObfuscatedAccountId = GetString(e, "obfuscatedAccountId");
                g.PurchaseState = GetInt(e, "purchaseState");
                g.Quantity = GetInt(e, "quantity");
                g.PurchaseTimeMillis = GetLong(e, "purchaseTime");
                g.Acknowledged = GetBool(e, "acknowledged");
                g.AutoRenewing = GetBool(e, "autoRenewing");
                return string.IsNullOrWhiteSpace(g.PurchaseToken) ? null : g;
            }
            catch { return null; }
        }

        /// <summary>Decode a JWS compact serialization's PAYLOAD without verifying it — for extracting the transaction id
        /// BEFORE verification (the reserve row needs the key). Never trust the content for anything else.</summary>
        public static JsonDocument DecodeJwsPayloadUnverified(string jws)
        {
            if (string.IsNullOrWhiteSpace(jws)) return null;
            var parts = jws.Split('.');
            if (parts.Length != 3) return null;
            try
            {
                var bytes = FromBase64Url(parts[1]);
                return JsonDocument.Parse(bytes);
            }
            catch { return null; }
        }

        public static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }

        // ---- keys ----

        /// <summary>Root of every idempotency key derived from a purchase: <c>iap:{id:N}</c> (36 chars).</summary>
        public static string IdemRoot(Guid purchaseId) => "iap:" + purchaseId.ToString("N");

        /// <summary>Wallet correlation id for reward line <paramref name="index"/>: <c>iap:{id:N}:{i}</c> — always ≤ 64.</summary>
        public static string LineKey(Guid purchaseId, int index)
        {
            var key = IdemRoot(purchaseId) + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (key.Length > 64) throw new InvalidOperationException("Store correlation id exceeds the wallet's 64-char budget: " + key);
            return key;
        }

        /// <summary>The ledger's ExternalRef (≤ 128): <c>{platform}:{storeTransactionId}</c> when it fits (Apple ids, order ids),
        /// else <c>{platform}:{purchaseId:N}</c> (a Google purchase token is 150+ chars; the token itself lives on the purchase row).</summary>
        public static string ExternalRef(StorePlatform platform, string storeTransactionId, Guid purchaseId)
        {
            var full = platform + ":" + (storeTransactionId ?? "");
            return full.Length <= 128 && !string.IsNullOrWhiteSpace(storeTransactionId) ? full : platform + ":" + purchaseId.ToString("N");
        }

        /// <summary>A store-side reference clamped to a column: the value itself when it fits, else a stable sha256-hex (64) of it
        /// prefixed so it is recognisably a hash. Used for <c>PlayerPassEntitlement.OriginalTransactionId</c> (96) and friends.</summary>
        public static string FitOrHash(string value, int maxLength, string hashPrefix = "h:")
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.Length <= maxLength) return value;
            var h = hashPrefix + Sha256Hex(value);
            return h.Length <= maxLength ? h : h.Substring(0, maxLength);
        }

        public static string Sha256Hex(string s)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))).ToLowerInvariant();
        }

        /// <summary>The obfuscated account id we hand the stores (Google <c>setObfuscatedAccountId</c>, Apple appAccountToken is the raw
        /// user Guid instead): a sha256-hex of the user id — 64 chars, the column's and Google's limit.</summary>
        public static string AccountHash(Guid userId) => Sha256Hex(userId.ToString("D"));

        public static DateTime? FromUnixMs(long? ms)
            => ms.HasValue && ms.Value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).UtcDateTime : (DateTime?)null;

        /// <summary>Clamp a raw receipt to the storage cap (UTF-8 bytes); a capped receipt is marked so it is never mistaken for a whole one.</summary>
        public static string Cap(string s, int maxBytes)
        {
            if (string.IsNullOrEmpty(s) || maxBytes <= 0) return s;
            if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
            var bytes = Encoding.UTF8.GetBytes(s);
            var cut = Encoding.UTF8.GetString(bytes, 0, Math.Max(0, maxBytes - 16));
            return cut + "…[truncated]";
        }

        // ---- tiny JSON helpers ----

        private static string GetString(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null) : null;

        private static int? GetInt(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : (int?)null;

        private static long? GetLong(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : (long?)null;

        private static bool? GetBool(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : (bool?)null;
    }
}
