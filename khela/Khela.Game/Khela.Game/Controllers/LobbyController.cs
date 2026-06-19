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
        private readonly IWebHostEnvironment env;

        public LobbyController(BlackjackTableManager tableManager, IWebHostEnvironment env)
        {
            this.tableManager = tableManager;
            this.env = env;
        }

        /// <summary>
        /// Browsable blackjack table list for the lobby (screen 3). Optional <c>?mode=</c> filters
        /// by variant (Classic / HiLo / BustOut / LuckyQueens).
        /// </summary>
        [HttpGet("blackjack")]
        public async Task<IActionResult> Blackjack([FromQuery] BlackjackMode? mode = null)
        {
            var tables = await tableManager.GetLobbyAsync(mode);
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
