using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Games.VideoPoker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Video Poker action channel (REST only — no table, no hub). Its own controller under <c>/api/videopoker/*</c>.
    /// Server-authoritative: the client picks a variant + bet and sends a hold; the server owns the shuffle, the
    /// paytable, and every balance change. <c>/deal</c> and <c>/draw</c> both return the SAME board contract
    /// (<see cref="VideoPokerBoard"/>).
    /// </summary>
    [ApiController]
    [Route("api/videopoker")]
    [Authorize]
    public class VideoPokerController : ControllerBase
    {
        private readonly VideoPokerService _service;
        public VideoPokerController(VideoPokerService service) => _service = service;

        /// <summary>The offered variants + their paytables (the menu screen). No auth-specific state.</summary>
        [HttpGet("variants")]
        [AllowAnonymous]
        public IActionResult Variants()
        {
            var list = VideoPokerVariants.List().Select(v => new VideoPokerVariantSummary
            {
                Id = v.Id,
                Name = v.Name,
                MinCoins = v.MinCoins,
                MaxCoins = v.MaxCoins,
                Rows = VideoPokerPaytableRows.For(v),
            }).ToList();
            return Ok(list);
        }

        [HttpPost("deal")]
        public Task<IActionResult> Deal([FromBody] DealVideoPokerRequest req)
            => Run(uid => _service.DealAsync(uid, req));

        [HttpPost("draw")]
        public Task<IActionResult> Draw([FromBody] DrawVideoPokerRequest req)
            => Run(uid => _service.DrawAsync(uid, req));

        [HttpGet("hand/{handId}")]
        public async Task<IActionResult> Hand(string handId)
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return Unauthorized("Missing user id.");
            var board = await _service.GetHandAsync(uid, handId);
            return board == null ? NotFound("Hand not found or expired.") : Ok(board);
        }

        /// <summary>Provably-fair proof for a settled hand: recomputes the whole hand from its revealed seed and reports,
        /// field by field, whether it reproduces the committed hashes and the recorded result. Public — the point of
        /// provably-fair is that anyone can check any hand by id.</summary>
        [HttpGet("verify/{handId}")]
        [AllowAnonymous]
        public async Task<IActionResult> Verify(string handId)
        {
            var result = await _service.VerifyAsync(handId);
            return result == null ? NotFound("Hand not found.") : Ok(result);
        }

        /// <summary>Resolve the caller's userId, run the op, and map manager errors to 400 (insufficient funds, bad
        /// input, expired hand) / 404 (null board).</summary>
        private async Task<IActionResult> Run(Func<string, Task<VideoPokerBoard>> op)
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return Unauthorized("Missing user id.");
            try
            {
                var board = await op(uid);
                return board == null ? NotFound("Hand not found or expired.") : Ok(board);
            }
            catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
        }
    }
}
