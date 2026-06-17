using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Khela.Common.Leaderboards;
using Khela.Game.Services.Leaderboards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class LeaderboardController : ControllerBase
    {
        private readonly ILeaderboardService _leaderboards;

        public LeaderboardController(ILeaderboardService leaderboards)
        {
            _leaderboards = leaderboards;
        }

        /// <summary>The active leaderboard boards (the browser list).</summary>
        [HttpGet]
        public async Task<IActionResult> Boards()
            => Ok(await _leaderboards.GetActiveBoardsAsync());

        /// <summary>
        /// One board's current page: top-N plus the caller's own rank. For a Regional board pass the
        /// region (ISO alpha-2); Global ignores it.
        /// </summary>
        [HttpGet("{code}")]
        public async Task<IActionResult> Page(
            string code,
            [FromQuery] LeaderboardScope scope = LeaderboardScope.Global,
            [FromQuery] string region = null,
            [FromQuery] int count = 50)
        {
            var page = await _leaderboards.GetPageAsync(code, scope, region ?? "", GetUserGuid(), count);
            return page == null ? NotFound(new { message = "Unknown leaderboard." }) : Ok(page);
        }

        private Guid? GetUserGuid()
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return Guid.TryParse(id, out var g) ? g : null;
        }
    }
}
