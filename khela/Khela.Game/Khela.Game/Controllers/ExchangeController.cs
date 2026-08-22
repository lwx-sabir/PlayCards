using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Exchange;
using Khela.Game.Services.Exchange;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Currency exchange (docs/EXCHANGE_SPEC.md): the pairs the admin authored, a server-side quote, and the exchange itself —
    /// idempotent on the client's request id. Every number comes from the server; the client only picks a pair and an amount.
    /// </summary>
    [ApiController]
    [Route("api/exchange")]
    [Authorize]
    public sealed class ExchangeController : ControllerBase
    {
        private readonly IExchangeService _exchange;

        public ExchangeController(IExchangeService exchange) { _exchange = exchange; }

        /// <summary>Every pair with MY availability, usage against the caps, and my balances.</summary>
        [HttpGet]
        public async Task<IActionResult> Catalog()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _exchange.GetCatalogAsync(me.Value));
        }

        /// <summary>"What would it cost?" — no writes.</summary>
        [HttpPost("quote")]
        public async Task<IActionResult> Quote([FromBody] ExchangeQuoteRequest request)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (request == null) return BadRequest(new { error = "Missing body." });
            return Ok(await _exchange.QuoteAsync(me.Value, request));
        }

        /// <summary>Do it. Answers 200 with <c>Ok</c>/<c>Error</c> in the body (the client keys its HUD repaint on the balances).</summary>
        [HttpPost]
        public async Task<IActionResult> Exchange([FromBody] ExchangeRequest request, CancellationToken ct)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (request == null) return BadRequest(new { error = "Missing body." });
            return Ok(await _exchange.ExchangeAsync(me.Value, request, ct));
        }

        /// <summary>My completed exchanges, newest first.</summary>
        [HttpGet("history")]
        public async Task<IActionResult> History([FromQuery] int take = 50)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _exchange.HistoryAsync(me.Value, take));
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
