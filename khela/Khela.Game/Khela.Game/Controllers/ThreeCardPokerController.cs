using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Games.ThreeCardPoker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Three Card Poker action channel (REST). Its own controller under <c>/api/threecard/*</c>, separate from
    /// Blackjack. Every state-changing endpoint returns the SAME masked projection — <see cref="ThreeCardPokerBoard"/> —
    /// so the client has one board contract regardless of the action sent, and can render immediately even if the
    /// SignalR push lags. Server-authoritative: the client only sends the single Play/Fold action + its bets.
    /// </summary>
    [ApiController]
    [Route("api/threecard")]
    [Authorize]
    public class ThreeCardPokerController : ControllerBase
    {
        private readonly ThreeCardPokerTableManager _tables;
        public ThreeCardPokerController(ThreeCardPokerTableManager tables) => _tables = tables;

        [HttpPost("create")]
        public async Task<IActionResult> Create([FromBody] CreateThreeCardTableRequest req)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var t = await _tables.CreateTableAsync(req.MaxPlayers, req.MaxSeatsPerUser, req.AnteMin, req.AnteMax, req.SideMin, req.SideMax);
            return Ok(ThreeCardPokerBoard.Build(t));
        }

        [HttpPost("{tableId}/join")]
        public Task<IActionResult> Join(string tableId, [FromBody] JoinThreeCardTableRequest req)
            => Run(uid => _tables.AddPlayerAsync(tableId, uid, req.Name, req.Image, req.SeatNumber));

        [HttpPost("{tableId}/leave/{seatNumber:int}")]
        public Task<IActionResult> Leave(string tableId, int seatNumber)
            => Run(uid => _tables.RemovePlayerAsync(tableId, uid, seatNumber));

        [HttpPost("{tableId}/bet")]
        public Task<IActionResult> Bet(string tableId, [FromBody] PlaceThreeCardBetsRequest req)
            => Run(uid => _tables.PlaceBetsAsync(tableId, uid, req));

        [HttpPost("{tableId}/deal")]
        public async Task<IActionResult> Deal(string tableId)
        {
            try
            {
                var t = await _tables.DealAsync(tableId);
                return t == null ? NotFound("Table not found or expired.") : Ok(ThreeCardPokerBoard.Build(t));
            }
            catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
        }

        [HttpPost("{tableId}/play/{seatNumber:int}")]
        public Task<IActionResult> Play(string tableId, int seatNumber)
            => Run(uid => _tables.PlayAsync(tableId, uid, seatNumber));

        [HttpPost("{tableId}/fold/{seatNumber:int}")]
        public Task<IActionResult> Fold(string tableId, int seatNumber)
            => Run(uid => _tables.FoldAsync(tableId, uid, seatNumber));

        [HttpGet("{tableId}/board")]
        public async Task<IActionResult> Board(string tableId)
        {
            var t = await _tables.GetTableAsync(tableId);
            return t == null ? NotFound("Table not found or expired.") : Ok(ThreeCardPokerBoard.Build(t));
        }

        /// <summary>Resolve the caller's userId, run the manager op, and return the board (mapping errors to 400/404).</summary>
        private async Task<IActionResult> Run(Func<string, Task<ThreeCardPokerTable>> op)
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return Unauthorized("Missing user id.");
            try
            {
                var t = await op(uid);
                return t == null ? NotFound("Table not found or expired.") : Ok(ThreeCardPokerBoard.Build(t));
            }
            catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
        }
    }
}
