using Khela.Common.Leaderboards;
using Khela.Game.Database;
using Khela.Game.Services.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Khela.Game.Services.Vip
{
    /// <summary>
    /// Monthly VIP tier review (Progression Spec §3.4 decay). Once per calendar month — restart-safe via a Redis SETNX
    /// month key so a redeploy can't double-run — it calls <see cref="IVipService.ReviewTierAsync"/> for every player
    /// currently above <c>None</c>: the gentle one-tier-max + hysteresis demotion (Bronze is the permanent floor;
    /// promotions are realized lazily on read, not here). Per-user scope so one bad row can't stall the batch.
    /// Best-effort — failures are logged, never fatal.
    /// </summary>
    public sealed class VipTierReviewService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IRedisService _redis;
        private readonly ILogger<VipTierReviewService> _logger;
        private readonly bool _enabled;

        public VipTierReviewService(IServiceScopeFactory scopes, IRedisService redis, IConfiguration config, ILogger<VipTierReviewService> logger)
        {
            _scopes = scopes;
            _redis = redis;
            _logger = logger;
            _enabled = config.GetValue("Vip:Enabled", true);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wake periodically; the calendar-month SETNX gates the actual run to once per month (so a frequent
            // restart never demotes more often than monthly — it can only DELAY a review, which is player-safe).
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromHours(12), stoppingToken); }
                catch (OperationCanceledException) { break; }

                if (!_enabled) continue;
                try { await RunMonthlyReviewAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "VIP tier review failed."); }
            }
        }

        private async Task RunMonthlyReviewAsync(CancellationToken ct)
        {
            // Once per calendar month: only the first waker each month claims the SETNX (TTL ~40d) and runs.
            var monthKey = $"vip:tierreview:{DateTime.UtcNow:yyyy-MM}";
            if (!await _redis.GetDatabase().StringSetAsync(monthKey, "1", TimeSpan.FromDays(40), When.NotExists))
                return;

            // Snapshot the userIds currently above None — only they can decay (promotions happen on read).
            List<Guid> ids;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                ids = await db.UserProfiles.AsNoTracking()
                    .Where(p => p.VipTier > VipTier.None)
                    .Select(p => p.UserId)
                    .ToListAsync(ct);
            }

            int reviewed = 0;
            foreach (var id in ids)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var scope = _scopes.CreateScope();
                    var vip = scope.ServiceProvider.GetRequiredService<IVipService>();
                    await vip.ReviewTierAsync(id);
                    reviewed++;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "VIP tier review failed for {UserId}", id); }
            }
            _logger.LogInformation("VIP monthly tier review complete: {Count} of {Total} players reviewed.", reviewed, ids.Count);
        }
    }
}
