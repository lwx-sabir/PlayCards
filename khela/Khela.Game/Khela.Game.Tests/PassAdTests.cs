using System;
using System.Linq;
using Khela.Game.Services.Ads;
using Khela.Game.Services.Pass;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the rewarded-ad catch-up path (docs/PASS_SPEC.md §5.6) — the parts that decide whether a "player watched
    /// an ad" claim is believable. The token binds a view to one player/cycle/day through an UNTRUSTED round trip
    /// (client → ad network → us), and the signed-portion extraction is what the network's signature is checked
    /// against. Pure: no HTTP, no DB, no crypto keys.
    /// </summary>
    public class PassAdTests
    {
        private const string Secret = "test-intent-secret-value";
        private static readonly DateTime Now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        private static readonly Guid User = Guid.Parse("8f14e45f-ceea-167a-5a36-dedd4bea2543");

        private static string Token(Guid? user = null, string pass = "monthly", string cycle = "2026-09", int node = 14,
            string secret = Secret, DateTime? now = null)
            => PassAdTokens.Issue(user ?? User, pass, cycle, node, secret, now ?? Now, "nonce123");

        // ---- the intent token ----

        [Fact]
        public void AValidTokenRoundTrips()
        {
            var intent = PassAdTokens.Verify(Token(), Secret, Now, out var error);
            Assert.Null(error);
            Assert.Equal(User, intent.UserId);
            Assert.Equal("monthly", intent.PassKey);
            Assert.Equal("2026-09", intent.CycleKey);
            Assert.Equal(14, intent.Node);
        }

        [Fact]
        public void ATokenSignedWithAnotherSecretIsRefused()
            => Assert.Null(PassAdTokens.Verify(Token(secret: "someone-elses-secret"), Secret, Now, out _));

        [Fact]
        public void RetargetingATokenToAnotherPlayerOrDayBreaksIt()
        {
            // The whole point: this value travels through the client and a third party, so every field is signed.
            var token = Token();
            var parts = token.Split('.');

            var otherUser = string.Join('.', parts[0], Guid.NewGuid().ToString("N"), parts[2], parts[3], parts[4], parts[5], parts[6], parts[7]);
            Assert.Null(PassAdTokens.Verify(otherUser, Secret, Now, out _));

            var otherDay = string.Join('.', parts[0], parts[1], parts[2], parts[3], "1", parts[5], parts[6], parts[7]);
            Assert.Null(PassAdTokens.Verify(otherDay, Secret, Now, out _));

            var otherCycle = string.Join('.', parts[0], parts[1], parts[2], "2026-10", parts[4], parts[5], parts[6], parts[7]);
            Assert.Null(PassAdTokens.Verify(otherCycle, Secret, Now, out _));
        }

        [Fact]
        public void AnExpiredOrFutureDatedTokenIsRefused()
        {
            var token = Token();
            Assert.NotNull(PassAdTokens.Verify(token, Secret, Now.AddMinutes(29), out _));
            Assert.Null(PassAdTokens.Verify(token, Secret, Now.Add(PassAdTokens.Lifetime).AddSeconds(1), out var error));
            Assert.Equal("Token expired.", error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("v1.only.three.parts")]
        [InlineData("v2.8f14e45fceea167a5a36dedd4bea2543.monthly.2026-09.14.9999999999.n.sig")]   // wrong version
        public void HostileInputIsRefusedWithoutThrowing(string token)
        {
            Assert.Null(PassAdTokens.Verify(token, Secret, Now, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        // ---- what the network actually signed ----

        [Fact]
        public void SignedPortion_IsEverythingBeforeTheSignatureParameter()
        {
            const string q = "?ad_network=5450213213286189855&ad_unit=1234&custom_data=tok&reward_amount=1" +
                             "&reward_item=coins&timestamp=1600000000&transaction_id=abc&signature=SIG&key_id=3335741209";
            var signed = AdSsvSigning.SignedPortion(q);

            Assert.StartsWith("ad_network=", signed);
            Assert.EndsWith("transaction_id=abc", signed);
            Assert.DoesNotContain("signature=", signed);
            Assert.DoesNotContain("key_id=", signed);      // AdMob signs nothing from &signature= onward
        }

        [Fact]
        public void SignedPortion_RefusesACallbackWithTheSignatureMovedOrMissing()
        {
            Assert.Null(AdSsvSigning.SignedPortion("?a=1&b=2"));                       // no signature at all
            Assert.Null(AdSsvSigning.SignedPortion("?signature=SIG&a=1"));             // first parameter: nothing signed
            Assert.Null(AdSsvSigning.SignedPortion(""));
            Assert.Null(AdSsvSigning.SignedPortion(null));
        }

        [Fact]
        public void ParseQuery_KeepsRawAndDecodedApart()
        {
            var q = "?custom_data=v1.abc%2Bdef&transaction_id=T-1&signature=SIG";
            var map = AdSsvSigning.ParseQuery(q);

            Assert.Equal("v1.abc+def", map["custom_data"]);                            // decoded for reading
            Assert.Contains("custom_data=v1.abc%2Bdef", AdSsvSigning.SignedPortion(q)); // raw bytes for verifying
            Assert.Equal("T-1", map["transaction_id"]);
        }

        [Fact]
        public void Base64UrlDecodesWithoutPadding()
        {
            var bytes = new byte[] { 251, 255, 190, 1, 2 };
            var encoded = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.Equal(bytes, AdSsvSigning.FromBase64Url(encoded));
        }

        // ---- the fail-closed default ----

        [Fact]
        public async System.Threading.Tasks.Task AnUnconfiguredDeploymentGrantsNothing()
        {
            var verifier = new DisabledAdSsvVerifier(Microsoft.Extensions.Logging.Abstractions.NullLogger<DisabledAdSsvVerifier>.Instance);
            var (ok, error) = await verifier.VerifyAsync(new AdSsvCallback { RawQuery = "?transaction_id=1&signature=x" });
            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void AdMobKeySetParses()
        {
            const string json = "{\"keys\":[{\"keyId\":3335741209,\"pem\":\"-----BEGIN PUBLIC KEY-----\\nAAAA\\n-----END PUBLIC KEY-----\\n\"}]}";
            var keys = AdMobSsvVerifier.ParseKeys(json);
            Assert.Single(keys);
            Assert.Contains("BEGIN PUBLIC KEY", keys["3335741209"]);
        }
    }
}
