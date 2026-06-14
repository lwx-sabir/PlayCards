using Khela.Common.Blackjack;
using Khela.Game.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class LobbyController : ControllerBase
    {
        private readonly BlackjackTableManager tableManager;

        public LobbyController(BlackjackTableManager tableManager)
        {
            this.tableManager = tableManager;
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
    }
}
