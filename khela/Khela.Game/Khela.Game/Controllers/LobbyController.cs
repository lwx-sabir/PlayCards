using Khela.Common.Blackjack;
using Khela.Game.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class LobbyController : ControllerBase
    {
        private readonly BlackjackTableManager tableManager;
        private readonly Khela.Game.Games.ThreeCardPoker.ThreeCardPokerTableManager threeCardTables;
        private readonly IWebHostEnvironment env;

        public LobbyController(BlackjackTableManager tableManager, Khela.Game.Games.ThreeCardPoker.ThreeCardPokerTableManager threeCardTables, IWebHostEnvironment env)
        {
            this.tableManager = tableManager;
            this.threeCardTables = threeCardTables;
            this.env = env;
        }

        /// <summary>
        /// Browsable blackjack table list for the lobby (screen 3). Optional <c>?mode=</c> filters by variant
        /// (Classic / HiLo / BustOut / LuckyQueens); <c>?minBet=&amp;maxBet=</c> restricts it to a single stake
        /// tier, which is what the client's bet-size filter sends.
        ///
        /// Within a tier the list is ordered fullest-first with completely empty tables LAST, so browsing lands a
        /// player among people rather than alone — and the final card is always a free table for anyone who does
        /// want to play by themselves.
        /// </summary>
        [HttpGet("blackjack")]
        public async Task<IActionResult> Blackjack(
            [FromQuery] BlackjackMode? mode = null,
            [FromQuery] decimal? minBet = null,
            [FromQuery] decimal? maxBet = null)
        {
            var tables = await tableManager.GetLobbyAsync(mode, minBet, maxBet);
            return Ok(tables);
        }

        /// <summary>
        /// The stake tiers the lobby offers, in order — what the client's bet-size filter is built from, so the
        /// filter follows the server's configuration instead of hardcoding brackets in the client.
        /// </summary>
        [HttpGet("blackjack/tiers")]
        public IActionResult BlackjackTiers()
            => Ok(tableManager.Tiers.Select(t => new { t.Key, t.MinBet, t.MaxBet }));

        /// <summary>Browsable Three Card Poker table list for the lobby (screen 3). Own endpoint per the
        /// plug-and-play, one-controller-per-game design — tops up the default house tables so it's never empty.</summary>
        [HttpGet("threecard")]
        public async Task<IActionResult> ThreeCard()
        {
            var tables = await threeCardTables.GetLobbyAsync();
            return Ok(tables);
        }

        /// <summary>
        /// DEV ONLY — wipe + re-create the seeded house tables (after editing DefaultTables). No auth so it's
        /// one click from Swagger; returns 404 outside Development so it can't ship.
        /// </summary>
        [AllowAnonymous]
        [HttpPost("dev/reseed")]
        public async Task<IActionResult> Reseed()
        {
            if (!env.IsDevelopment()) return NotFound();
            var tables = await tableManager.ReseedDefaultTablesAsync();
            return Ok(tables);
        }
    }
}
