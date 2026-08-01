using System.Diagnostics;
using Khela.Game.Database;
using Khela.Game.Games.ThreeCardPoker;
using Khela.Game.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Khela.Game.Controllers
{
    /// <summary>
    /// Public liveness + coarse world-state probe. <c>GET /api/health</c> is unauthenticated so a load balancer /
    /// uptime monitor can hit it. It reports process liveness, dependency pings (MySQL + Redis), and a bit of live
    /// world state (active tables / occupied seats / rounds running for each built game). Returns 200 when both
    /// dependencies answer, 503 when either is down (so a probe actually detects an outage). The world-state block
    /// is best-effort and coarse — occupied-seat COUNTS only, never player identities — and never fails the probe.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/health")]
    public class HealthController : ControllerBase
    {
        // Evaluated once on first access; process start (not machine boot). Drives the uptime figure.
        private static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        private readonly AppDbContext _db;
        private readonly IConnectionMultiplexer _redis;
        private readonly BlackjackTableManager _blackjack;
        private readonly ThreeCardPokerTableManager _threeCard;
        private readonly ILogger<HealthController> _logger;

        public HealthController(AppDbContext db, IConnectionMultiplexer redis, BlackjackTableManager blackjack,
            ThreeCardPokerTableManager threeCard, ILogger<HealthController> logger)
        {
            _db = db;
            _redis = redis;
            _blackjack = blackjack;
            _threeCard = threeCard;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var dbOk = await PingDatabaseAsync();
            var redisOk = PingRedis();

            var world = new
            {
                blackjack = await GatherBlackjackAsync(),
                threeCardPoker = await GatherThreeCardAsync()
            };

            var healthy = dbOk && redisOk;
            var payload = new
            {
                status = healthy ? "healthy" : "degraded",
                timeUtc = DateTime.UtcNow.ToString("o"),
                uptimeSeconds = (long)(DateTime.UtcNow - ProcessStartUtc).TotalSeconds,
                version = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "unknown",
                deps = new
                {
                    database = dbOk ? "ok" : "down",
                    redis = redisOk ? "ok" : "down"
                },
                world
            };

            // 503 on a dependency outage so an uptime monitor / load balancer treats the node as unhealthy.
            return healthy ? Ok(payload) : StatusCode(StatusCodes.Status503ServiceUnavailable, payload);
        }

        // A cheap, bounded connectivity check — not a query. CanConnectAsync opens/validates a connection and
        // swallows provider exceptions into a bool; any throw is treated as "down" so the probe never 500s.
        private async Task<bool> PingDatabaseAsync()
        {
            try { return await _db.Database.CanConnectAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Health: database ping failed."); return false; }
        }

        private bool PingRedis()
        {
            try
            {
                if (!_redis.IsConnected) return false;
                _redis.GetDatabase().Ping();
                return true;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Health: redis ping failed."); return false; }
        }

        // ---- world-state gatherers (best-effort; a Redis hiccup yields zeros, never a failed probe) ----

        private async Task<object> GatherBlackjackAsync()
        {
            try
            {
                var ids = await _blackjack.GetActiveTableIdsAsync();
                int tables = 0, seated = 0, rounds = 0;
                foreach (var id in ids)
                {
                    var t = await _blackjack.GetTableAsync(id);
                    if (t == null) continue;   // TTL-expired id still in the lobby index; ignore
                    tables++;
                    seated += t.Seats.Count(s => s.Player != null);
                    if (t.RoundInProgress) rounds++;
                }
                return new { tables, seatedPlayers = seated, roundsInProgress = rounds };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health: blackjack world-state gather failed.");
                return new { tables = 0, seatedPlayers = 0, roundsInProgress = 0, error = true };
            }
        }

        private async Task<object> GatherThreeCardAsync()
        {
            try
            {
                var ids = await _threeCard.GetActiveTableIdsAsync();
                int tables = 0, seated = 0, rounds = 0;
                foreach (var id in ids)
                {
                    var t = await _threeCard.GetTableAsync(id);
                    if (t == null) continue;
                    tables++;
                    seated += t.Seats.Count(s => s.Player != null);
                    if (t.RoundInProgress) rounds++;
                }
                return new { tables, seatedPlayers = seated, roundsInProgress = rounds };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health: 3CP world-state gather failed.");
                return new { tables = 0, seatedPlayers = 0, roundsInProgress = 0, error = true };
            }
        }
    }
}
