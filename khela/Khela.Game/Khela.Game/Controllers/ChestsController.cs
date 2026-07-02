using System.Threading.Tasks;
using Khela.Game.Services.Chests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Read-only chest catalog for clients and editor tools — chest identity + display text only (no reward ranges, so
    /// it's safe to expose). Anonymous so an editor "pick a chest" tool can fetch the list without a player token.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ChestsController : ControllerBase
    {
        private readonly IChestService _chests;

        public ChestsController(IChestService chests) => _chests = chests;

        /// <summary>List the available chests (key, tier, title, description). Wrapped as { chests: [...] } for easy parsing.</summary>
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> List() => Ok(new { chests = await _chests.ListAsync() });
    }
}
