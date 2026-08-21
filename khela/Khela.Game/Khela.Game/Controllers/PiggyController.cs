using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Common.Piggy;
using Khela.Game.Services.Piggy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// The piggy bank: a chip pack whose unlock is paced by play. It fills from wagering and is released only by
    /// paying, so this endpoint is read-only for now — the break lands with the IAP receipt seam.
    ///
    /// Deliberately NOT folded into the blackjack board snapshot. That snapshot is rebuilt and broadcast on every
    /// table change; a per-player database read inside it would put this feature in the hottest loop the server has.
    /// The client refreshes the bank on round end, beside the wallet refresh it already does.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class PiggyController : ControllerBase
    {
        private readonly IPiggyService _piggy;

        public PiggyController(IPiggyService piggy)
        {
            _piggy = piggy;
        }

        /// <summary>The player's bank: how full, which tier, and whether it can be bought yet.
        /// <c>Enabled = false</c> means the feature is off — the client hides the widget.</summary>
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _piggy.GetStateAsync(me.Value));
        }

        /// <summary>
        /// The player is looking at a FULL bank — start their countdown.
        ///
        /// Call this at the moment the ready state is actually rendered, and nowhere else. It is deliberately not
        /// folded into the GET: a read happens for all sorts of reasons the player never sees, and starting a
        /// deadline on one of those would take away an offer they were never shown.
        ///
        /// Safe to call repeatedly — only the first sighting starts the clock.
        /// </summary>
        [HttpPost("seen")]
        public async Task<IActionResult> Seen()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _piggy.MarkSeenAsync(me.Value));
        }

        /// <summary>
        /// The chips have finished flying into the pig — bank the acknowledgement so the next celebration measures
        /// from here.
        ///
        /// Called AFTER the animation, not before: acknowledging first would lose the whole delta if the client died
        /// mid-burst, and a player who never saw their chips arrive would never be shown them again.
        /// </summary>
        /// <summary>
        /// Buy the bank. The payout, the eligibility and the price are all decided server-side; the body carries
        /// only WHICH offer was taken and the store's order id.
        ///
        /// The order id is the idempotency key, and it is required: a store will deliver the same purchase more than
        /// once, and a payout keyed on anything else would mint chips every time one arrived.
        ///
        /// Refusals come back as <c>Ok = false</c> with a reason rather than an HTTP error - the same convention the
        /// pass and daily endpoints use, so the client can show the reason verbatim instead of guessing from a code.
        /// </summary>
        [HttpPost("break")]
        public async Task<IActionResult> Break([FromBody] PiggyBreakRequest request)
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            if (request == null) return BadRequest();

            return Ok(await _piggy.BreakAsync(me.Value, request.Option, request.PurchaseId));
        }

        /// <summary>What the client is allowed to say about a break: which offer, and which order paid for it.</summary>
        public sealed class PiggyBreakRequest
        {
            public PiggyBreakOption Option { get; set; }

            /// <summary>The store's order id. Required - it is what makes the payout idempotent.</summary>
            public string PurchaseId { get; set; }
        }

        [HttpPost("celebrated")]
        public async Task<IActionResult> Celebrated()
        {
            var me = CallerId();
            if (me == null) return Unauthorized();
            return Ok(await _piggy.MarkCelebratedAsync(me.Value));
        }

        private Guid? CallerId()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : (Guid?)null;
        }
    }
}
