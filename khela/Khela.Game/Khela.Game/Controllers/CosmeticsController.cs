using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Services.Cosmetics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// The cosmetics shop: catalog (with the caller's ownership), wallet-debited purchases, and the admin catalog
    /// import (the Unity Cosmetic Exporter's JSON). Server-authoritative — see docs/AVATAR_SHOP_SPEC.md.
    /// </summary>
    [ApiController]
    [Route("api/shop/cosmetics")]
    [Authorize]
    public sealed class CosmeticsController : ControllerBase
    {
        private readonly ICosmeticsService _cosmetics;
        public CosmeticsController(ICosmeticsService cosmetics) => _cosmetics = cosmetics;

        /// <summary>The enabled catalog + owned flags for the caller (starters show as owned).</summary>
        [HttpGet]
        public async Task<IActionResult> Catalog()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(new { skus = await _cosmetics.GetCatalogAsync(me.Value) });
        }

        public sealed class BuyRequest
        {
            /// <summary>Client-generated idempotency key — resending the same purchase never double-charges.</summary>
            public string CorrelationId { get; set; }
        }

        /// <summary>Buy a SKU. Idempotent on CorrelationId; buying something you own is a no-op success.</summary>
        [HttpPost("{id}/buy")]
        public async Task<IActionResult> Buy(string id, [FromBody] BuyRequest req)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            var result = await _cosmetics.BuyAsync(me.Value, id, req?.CorrelationId);
            return result.Ok ? Ok(result) : BadRequest(result);
        }

        /// <summary>Admin: upsert the authored catalog JSON (khela/catalog/cosmetics.json from the exporter tool).</summary>
        [HttpPost("import")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> Import([FromBody] CatalogImportDto file)
        {
            var result = await _cosmetics.ImportAsync(file);
            return result.Errors.Count == 0 ? Ok(result) : BadRequest(result);
        }

        /// <summary>The SKU's baked 3D product shot. Anonymous + cacheable — shop art isn't a secret, and the web
        /// admin (separate cookie auth) and CDNs can hotlink it.</summary>
        [HttpGet("{id}/icon")]
        [AllowAnonymous]
        public async Task<IActionResult> Icon(string id)
        {
            var (png, updatedAt) = await _cosmetics.GetIconAsync(id);
            if (png == null) return NotFound();
            Response.Headers.CacheControl = "public, max-age=300";
            if (updatedAt != null) Response.Headers.ETag = $"\"{updatedAt.Value.Ticks}\"";
            return File(png, "image/png");
        }

        /// <summary>Admin: store/replace a SKU's icon. Raw PNG body (Content-Type image/png), ≤1MB.</summary>
        [HttpPost("{id}/icon")]
        [Authorize(Policy = "Admin")]
        [RequestSizeLimit(2 * 1024 * 1024)]
        public async Task<IActionResult> UploadIcon(string id)
        {
            using var ms = new System.IO.MemoryStream();
            await Request.Body.CopyToAsync(ms);
            var err = await _cosmetics.SetIconAsync(id, ms.ToArray());
            return err == null ? Ok(new { ok = true }) : BadRequest(new { message = err });
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
