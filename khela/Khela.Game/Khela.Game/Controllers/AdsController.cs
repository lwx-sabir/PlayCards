using System.Threading;
using System.Threading.Tasks;
using Khela.Game.Services.Ads;
using Khela.Game.Services.Pass;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// The ad network's server-to-server reward callback (docs/PASS_SPEC.md §5.6).
    ///
    /// <b>Anonymous by necessity, authenticated by signature.</b> The caller is Google/Unity/ironSource, not a player,
    /// so there is no session to check — the request is trusted only because <see cref="IAdSsvVerifier"/> verifies the
    /// network's cryptographic signature over the exact query string, and because it carries a token this server
    /// issued. Nothing here reads a player's identity from the request body.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class AdsController : ControllerBase
    {
        private readonly IPassAdService _ads;
        private readonly Services.Daily.IDailyAdService _dailyAds;

        public AdsController(IPassAdService ads, Services.Daily.IDailyAdService dailyAds)
        {
            _ads = ads; _dailyAds = dailyAds;
        }

        /// <summary>
        /// Rewarded-ad SSV. Networks call this with GET and sign the query string, so the RAW query is what gets
        /// verified — never a re-serialized version of it, which would change the bytes and fail (or worse, pass on
        /// the wrong data).
        ///
        /// Always answers 200 to a well-formed call: a network that gets an error status will retry the same
        /// transaction for hours, and a rejected callback is a decision, not an outage.
        /// </summary>
        [HttpGet("ssv")]
        [HttpPost("ssv")]
        public async Task<IActionResult> Ssv(CancellationToken ct)
        {
            var raw = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
            var callback = new AdSsvCallback
            {
                RawQuery = raw,
                Query = AdSsvSigning.ParseQuery(raw),
            };

            var (ok, error) = await _ads.CreditAsync(callback, ct);
            return Ok(new { ok, error });
        }

        /// <summary>
        /// The same thing for the DAILY LOGIN ladder.
        ///
        /// A separate URL rather than one endpoint that sniffs the token: each ladder is a different ad placement, and
        /// networks configure the callback per placement anyway. It also means a misconfigured placement fails loudly
        /// on the wrong ladder instead of silently crediting the other one — the token's scope is checked again inside,
        /// so even a mixed-up URL cannot credit across ladders.
        /// </summary>
        [HttpGet("ssv/daily")]
        [HttpPost("ssv/daily")]
        public async Task<IActionResult> SsvDaily(CancellationToken ct)
        {
            var raw = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
            var callback = new AdSsvCallback
            {
                RawQuery = raw,
                Query = AdSsvSigning.ParseQuery(raw),
            };

            var (ok, error) = await _dailyAds.CreditAsync(callback, ct);
            return Ok(new { ok, error });
        }
    }
}
