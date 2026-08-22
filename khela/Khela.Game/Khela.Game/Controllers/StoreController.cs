using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// The store (docs/IAP_SPEC.md §5): catalog for this platform, purchase intent, redeem (the money path — server-verified,
    /// idempotent on the store transaction), restore, history; admin catalog/purchase ops. Webhooks (RTDN / App Store
    /// Server Notifications) land here too when the subscription phase ships.
    ///
    /// Convention: 200-always with <c>Ok</c> + <c>Status</c> in the body — the client decides what to do with the store
    /// order from the status, never from an HTTP code. 400 only for a malformed request.
    /// </summary>
    [ApiController]
    [Route("api/store")]
    [Authorize]
    public class StoreController : ControllerBase
    {
        private readonly IStoreCatalogService _catalog;
        private readonly IStorePurchaseService _purchases;
        private readonly IRedisService _redis;
        private readonly AppDbContext _db;

        public StoreController(IStoreCatalogService catalog, IStorePurchaseService purchases, IRedisService redis, AppDbContext db)
        {
            _catalog = catalog; _purchases = purchases; _redis = redis; _db = db;
        }

        /// <summary>The catalog as THIS platform sees it: store-product ids to buy, what each pays (display), per-user availability.</summary>
        [HttpGet("catalog")]
        public async Task<IActionResult> Catalog([FromQuery] StorePlatform platform = StorePlatform.Unknown)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (platform == StorePlatform.Unknown) platform = StorePlatform.Fake;
            return Ok(await _catalog.GetCatalogAsync(platform, me.Value));
        }

        /// <summary>"May I buy this now?" — call before opening the store sheet. Enforces limits where a refusal costs nothing.</summary>
        [HttpPost("intent")]
        public async Task<IActionResult> Intent([FromBody] StoreIntentRequest request, CancellationToken ct)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (request == null) return BadRequest(new { error = "Missing body." });
            return Ok(await _purchases.IntentAsync(me.Value, request, ct));
        }

        /// <summary>The money path. Hand over the store receipt; the server verifies with the store and credits the wallet.
        /// Safe to repeat with the same receipt at any time (AlreadyGranted).</summary>
        [HttpPost("redeem")]
        [RequestSizeLimit(128 * 1024)]
        public async Task<IActionResult> Redeem([FromBody] RedeemPurchaseRequest request, CancellationToken ct)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (request == null) return BadRequest(new { error = "Missing body." });
            return Ok(await _purchases.RedeemAsync(me.Value, request, ct));
        }

        /// <summary>Restore purchases: re-run a batch of receipts through the idempotent redeem path.</summary>
        [HttpPost("restore")]
        [RequestSizeLimit(1024 * 1024)]
        public async Task<IActionResult> Restore([FromBody] StoreRestoreRequest request, CancellationToken ct)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (request == null) return BadRequest(new { error = "Missing body." });
            return Ok(await _purchases.RestoreAsync(me.Value, request, ct));
        }

        /// <summary>My purchase history (support / restore UI).</summary>
        [HttpGet("purchases")]
        public async Task<IActionResult> Purchases([FromQuery] int take = 50, CancellationToken ct = default)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _purchases.GetHistoryAsync(me.Value, take, ct));
        }

        // ---------------------------------------------------------------- admin

        /// <summary>The full catalog document (the Redis override or the code defaults).</summary>
        [HttpGet("admin/catalog")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> AdminCatalog() => Ok(await _catalog.GetConfigAsync());

        /// <summary>Replace the catalog document. Validated fail-closed; 400 with the first error on failure.</summary>
        [HttpPut("admin/catalog")]
        [Authorize(Policy = "Admin")]
        [RequestSizeLimit(2 * 1024 * 1024)]
        public async Task<IActionResult> AdminSaveCatalog([FromBody] StoreCatalogConfig cfg)
        {
            var err = await _catalog.SaveAsync(cfg);
            return err == null ? Ok(new { ok = true }) : BadRequest(new { ok = false, error = err });
        }

        /// <summary>Queue a re-drive of a stuck/invalid purchase row; the reconciler executes it.</summary>
        [HttpPost("admin/redrive/{id:guid}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> AdminRedrive(Guid id, [FromQuery] bool now = false, CancellationToken ct = default)
        {
            if (now) return Ok(await _purchases.RedriveAsync(id, ct));
            await _redis.GetDatabase().ListRightPushAsync(StoreReconciliationService.RedriveQueueKey, id.ToString("D"));
            return Ok(new { ok = true, queued = id });
        }

        /// <summary>Mark a purchase refunded by hand (support) — applies the refund policy. Idempotent.</summary>
        [HttpPost("admin/refund/{id:guid}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> AdminRefund(Guid id, [FromQuery] string reason = null, CancellationToken ct = default)
            => Ok(new { ok = await _purchases.MarkRefundedAsync(id, "admin", reason ?? "manual", ct: ct) });

        /// <summary>Purchases ledger (newest first) with simple filters.</summary>
        [HttpGet("admin/purchases")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> AdminPurchases([FromQuery] Guid? userId = null, [FromQuery] StorePurchaseStatus? status = null,
            [FromQuery] StorePlatform? platform = null, [FromQuery] bool? test = null, [FromQuery] int take = 100, CancellationToken ct = default)
        {
            take = Math.Clamp(take, 1, 500);
            var q = _db.StorePurchases.AsNoTracking().AsQueryable();
            if (userId.HasValue) q = q.Where(s => s.UserId == userId.Value);
            if (status.HasValue) q = q.Where(s => s.Status == status.Value);
            if (platform.HasValue) q = q.Where(s => s.Platform == platform.Value);
            if (test.HasValue) q = q.Where(s => s.IsTest == test.Value);
            var rows = await q.OrderByDescending(s => s.CreatedAt).Take(take).Select(s => new
            {
                s.Id, s.UserId, s.Platform, s.ProductId, s.StoreProductId, s.StoreOrderId, s.ProductType, s.Status, s.IsTest, s.Environment,
                s.UsdReference, s.ClientPriceMicros, s.ClientPriceCurrency, s.RegionCode, s.CountryCode, s.Attempts, s.LastError,
                s.CreatedAt, s.VerifiedAt, s.GrantedAt, s.CompletedAt, s.AcknowledgedAt, s.RefundedAt, s.RefundSource, s.SubscriptionExpiresAt,
            }).ToListAsync(ct);
            return Ok(rows);
        }

        /// <summary>One purchase in full (receipt, verifier evidence, fulfilment, events).</summary>
        [HttpGet("admin/purchases/{id:guid}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> AdminPurchase(Guid id, CancellationToken ct = default)
        {
            var row = await _db.StorePurchases.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (row == null) return NotFound();
            var events = await _db.StoreEvents.AsNoTracking().Where(e => e.PurchaseId == id).OrderByDescending(e => e.ReceivedAt).Take(50).ToListAsync(ct);
            return Ok(new { purchase = row, events });
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
