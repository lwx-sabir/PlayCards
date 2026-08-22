using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Services.Store;
using Khela.Game.Services.Store.Verification;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Store webhooks (docs/IAP_SPEC.md §5.5). Anonymous by necessity, verified by construction:
    /// <list type="bullet">
    /// <item><b>Google Play RTDN</b> arrives as a Pub/Sub PUSH. The push subscription is configured with <c>?token=</c> = the
    /// shared secret <c>Store:GooglePlay:PubSubToken</c> (no token configured ⇒ the endpoint refuses everything — fail closed).
    /// Nothing in the message is trusted for money: a refund/void marks the row and applies the refund policy; a subscription
    /// event makes the server re-ask Google through the verifier.</item>
    /// <item><b>Apple App Store Server Notifications v2</b> carry a <c>signedPayload</c> JWS that the Apple verifier validates
    /// against Apple's root certificate before anything is read from it.</item>
    /// </list>
    /// Every notification is recorded in <c>StoreEvents</c> idempotently; replays are acknowledged and ignored. Both endpoints
    /// answer 2xx quickly (the stores retry on anything else) and never leak why something was ignored.
    /// </summary>
    [ApiController]
    [Route("api/store/webhooks")]
    [AllowAnonymous]
    public class StoreWebhooksController : ControllerBase
    {
        private readonly IStoreWebhookService _webhooks;
        private readonly StoreVerifierRegistry _verifiers;
        private readonly IOptionsMonitor<StoreOptions> _options;
        private readonly ILogger<StoreWebhooksController> _logger;

        public StoreWebhooksController(IStoreWebhookService webhooks, StoreVerifierRegistry verifiers, IOptionsMonitor<StoreOptions> options, ILogger<StoreWebhooksController> logger)
        {
            _webhooks = webhooks; _verifiers = verifiers; _options = options; _logger = logger;
        }

        /// <summary>Google Play Real-time Developer Notifications (Pub/Sub push). Configure the push endpoint as
        /// <c>https://…/api/store/webhooks/google?token=&lt;Store:GooglePlay:PubSubToken&gt;</c>.</summary>
        [HttpPost("google")]
        [RequestSizeLimit(256 * 1024)]
        public async Task<IActionResult> Google([FromQuery] string token, CancellationToken ct)
        {
            var expected = _options.CurrentValue.GooglePlay.PubSubToken;
            if (string.IsNullOrWhiteSpace(expected) || !FixedTimeEquals(token, expected))
            {
                _logger.LogWarning("RTDN push refused: {Reason}", string.IsNullOrWhiteSpace(expected) ? "no Store:GooglePlay:PubSubToken configured" : "bad token");
                return Unauthorized();
            }
            string body;
            using (var reader = new StreamReader(Request.Body)) body = await reader.ReadToEndAsync(ct);
            var parsed = _webhooks.ParseGooglePush(body);
            if (parsed == null) return BadRequest(new { error = "Not a Pub/Sub push message." });

            var outcome = await _webhooks.HandleGoogleAsync(parsed.Value.MessageId, parsed.Value.RawData, parsed.Value.Notification, ct);
            _logger.LogInformation("RTDN {MessageId}: {Outcome}", parsed.Value.MessageId, outcome);
            return NoContent();   // 204 = ack; Pub/Sub retries only on non-2xx
        }

        /// <summary>Apple App Store Server Notifications v2 (<c>{"signedPayload":"…"}</c>). Configure the URL in App Store Connect ▸ App Information (production + sandbox).</summary>
        [HttpPost("apple")]
        [RequestSizeLimit(256 * 1024)]
        public async Task<IActionResult> Apple(CancellationToken ct)
        {
            string body;
            using (var reader = new StreamReader(Request.Body)) body = await reader.ReadToEndAsync(ct);
            string signedPayload = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("signedPayload", out var sp) && sp.ValueKind == JsonValueKind.String) signedPayload = sp.GetString();
            }
            catch { }
            if (string.IsNullOrWhiteSpace(signedPayload)) return BadRequest(new { error = "Missing signedPayload." });

            if (_verifiers.Resolve(StorePlatform.AppStore) is not AppStoreReceiptVerifier apple || !apple.IsConfigured)
            {
                _logger.LogWarning("Apple notification received but the App Store verifier is not configured/registered.");
                return StatusCode(503);   // Apple retries; nothing is acknowledged we can't verify
            }
            var notification = await apple.DecodeNotificationAsync(signedPayload, ct);
            if (notification == null) return Unauthorized();   // signature/claims rejected — never trust the contents

            var outcome = await _webhooks.HandleAppleAsync(notification, ct);
            _logger.LogInformation("ASN {Uuid} {Type}/{Subtype}: {Outcome}", notification.NotificationUuid, notification.NotificationType, notification.Subtype, outcome);
            return Ok();
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            var x = System.Text.Encoding.UTF8.GetBytes(a);
            var y = System.Text.Encoding.UTF8.GetBytes(b);
            return x.Length == y.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
        }
    }
}
