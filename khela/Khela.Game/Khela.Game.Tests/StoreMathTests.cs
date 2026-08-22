using System;
using Khela.Common.Store;
using Khela.Game.Services.Store;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>Receipt parsing and the idempotency-key rules of the store spine — pure, DB-free.</summary>
    public class StoreMathTests
    {
        private const string GoogleUnified =
            "{\"Store\":\"GooglePlay\",\"TransactionID\":\"GPA.3312-1234-5678-90123\",\"Payload\":\"{\\\"json\\\":\\\"{\\\\\\\"orderId\\\\\\\":\\\\\\\"GPA.3312-1234-5678-90123\\\\\\\",\\\\\\\"packageName\\\\\\\":\\\\\\\"com.casuallabinteractive.khela\\\\\\\",\\\\\\\"productId\\\\\\\":\\\\\\\"chips_01\\\\\\\",\\\\\\\"purchaseTime\\\\\\\":1756000000000,\\\\\\\"purchaseState\\\\\\\":0,\\\\\\\"purchaseToken\\\\\\\":\\\\\\\"abcdefghijklmnopqrstuvwxyz0123456789.AO-J1OxTOKEN\\\\\\\",\\\\\\\"obfuscatedAccountId\\\\\\\":\\\\\\\"deadbeef\\\\\\\",\\\\\\\"quantity\\\\\\\":1,\\\\\\\"acknowledged\\\\\\\":false}\\\",\\\"signature\\\":\\\"SIG==\\\",\\\"skuDetails\\\":[]}\"}";

        [Fact]
        public void UnifiedReceipt_Parses_Google()
        {
            var r = StoreMath.ParseUnifiedReceipt(GoogleUnified);
            Assert.NotNull(r);
            Assert.Equal("GooglePlay", r.Store);
            Assert.Equal("GPA.3312-1234-5678-90123", r.TransactionId);
            var g = StoreMath.ParseGooglePayload(r.Payload);
            Assert.NotNull(g);
            Assert.Equal("abcdefghijklmnopqrstuvwxyz0123456789.AO-J1OxTOKEN", g.PurchaseToken);
            Assert.Equal("chips_01", g.ProductId);
            Assert.Equal("com.casuallabinteractive.khela", g.PackageName);
            Assert.Equal("GPA.3312-1234-5678-90123", g.OrderId);
            Assert.Equal(0, g.PurchaseState);
            Assert.Equal("deadbeef", g.ObfuscatedAccountId);
            Assert.Equal(1, g.Quantity);
            Assert.False(g.Acknowledged);
            Assert.Equal("SIG==", g.Signature);
            Assert.Equal(new DateTime(2025, 8, 24, 1, 46, 40, DateTimeKind.Utc), StoreMath.FromUnixMs(g.PurchaseTimeMillis));
        }

        [Fact]
        public void UnifiedReceipt_RejectsGarbage()
        {
            Assert.Null(StoreMath.ParseUnifiedReceipt(null));
            Assert.Null(StoreMath.ParseUnifiedReceipt("not json"));
            Assert.Null(StoreMath.ParseUnifiedReceipt("{\"TransactionID\":\"x\"}"));      // no Store
            Assert.Null(StoreMath.ParseGooglePayload("{\"json\":\"{}\"}"));                // no purchaseToken
            Assert.Null(StoreMath.ParseGooglePayload("{\"signature\":\"x\"}"));
        }

        [Fact]
        public void FakeReceipt_Parses()
        {
            var r = StoreMath.ParseUnifiedReceipt("{\"Store\":\"fake\",\"TransactionID\":\"7f1e\",\"Payload\":\"{ \\\"this\\\" : \\\"is a fake receipt\\\" }\"}");
            Assert.Equal("fake", r.Store);
            Assert.Equal("7f1e", r.TransactionId);
        }

        [Fact]
        public void Jws_PayloadDecodes_Unverified()
        {
            // header.payload.signature — only the payload is read (never trusted for anything but the key)
            static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var jws = B64("{\"alg\":\"ES256\"}") + "." + B64("{\"transactionId\":\"2000000123\",\"productId\":\"chips_01\"}") + "." + B64("sig");
            using var doc = StoreMath.DecodeJwsPayloadUnverified(jws);
            Assert.NotNull(doc);
            Assert.Equal("2000000123", doc.RootElement.GetProperty("transactionId").GetString());
            Assert.Null(StoreMath.DecodeJwsPayloadUnverified("two.parts"));
        }

        [Fact]
        public void Keys_FitTheWalletBudget()
        {
            var id = Guid.NewGuid();
            Assert.Equal(36, StoreMath.IdemRoot(id).Length);
            Assert.StartsWith("iap:", StoreMath.LineKey(id, 0));
            Assert.True(StoreMath.LineKey(id, 999).Length <= 64);
            Assert.True(StoreMath.LineKey(id, int.MaxValue).Length <= 64);   // 36 + ':' + 10 digits = 47, always inside the budget
            Assert.Equal(StoreMath.LineKey(id, 3), StoreMath.LineKey(id, 3));  // a fixed function of (purchase, position) — retries land on the same key
        }

        [Fact]
        public void ExternalRef_UsesTheStoreIdWhenItFits_ElseThePurchaseId()
        {
            var id = Guid.NewGuid();
            Assert.Equal("AppStore:2000000123", StoreMath.ExternalRef(StorePlatform.AppStore, "2000000123", id));
            var longToken = new string('t', 200);
            var r = StoreMath.ExternalRef(StorePlatform.GooglePlay, longToken, id);
            Assert.True(r.Length <= 128);
            Assert.Equal("GooglePlay:" + id.ToString("N"), r);
        }

        [Fact]
        public void FitOrHash_KeepsShortValues_HashesLongOnes_Stably()
        {
            Assert.Equal("GPA.1-2-3", StoreMath.FitOrHash("GPA.1-2-3", 96));
            var token = new string('a', 200);
            var h1 = StoreMath.FitOrHash(token, 96);
            var h2 = StoreMath.FitOrHash(token, 96);
            Assert.Equal(h1, h2);
            Assert.True(h1.Length <= 96);
            Assert.StartsWith("h:", h1);
        }

        [Fact]
        public void AccountHash_Is64HexChars_AndStable()
        {
            var u = Guid.Parse("7a6645de-8918-4624-be12-347d19811b77");
            var h = StoreMath.AccountHash(u);
            Assert.Equal(64, h.Length);
            Assert.Equal(h, StoreMath.AccountHash(u));
            Assert.Matches("^[0-9a-f]{64}$", h);
        }

        [Fact]
        public void Cap_TruncatesAndMarks()
        {
            var s = new string('x', 1000);
            Assert.Same(s, StoreMath.Cap(s, 2000));
            var capped = StoreMath.Cap(s, 100);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(capped) <= 100 + 16);
            Assert.EndsWith("[truncated]", capped);
        }
    }
}
