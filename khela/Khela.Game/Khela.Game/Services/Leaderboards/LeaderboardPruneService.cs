using Khela.Game.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Leaderboards
{
    /// <summary>
    /// Nightly prune of old <c>PlayerDailyStat</c> rows beyond the retention window (default 90 days — covers
    /// daily/weekly/monthly + "last month"). All-time stats live in UserGameStats/UserProfile and are NOT touched.
    /// Bulk delete via ExecuteDelete (no entity load). Best-effort; failures are logged, never fatal.
    /// </summary>
    public sealed class LeaderboardPruneService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<LeaderboardPruneService> _logger;
        private readonly int _retentionDays;

        public LeaderboardPruneService(IServiceScopeFactory scopes, IConfiguration config, ILogger<LeaderboardPruneService> logger)
        {
            _scopes = scopes;
            _logger = logger;
            _retentionDays = Math.Max(31, config.GetValue("Leaderboard:DailyStatRetentionDays", 90));   // never below a month
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var cutoff = DateTime.UtcNow.Date.AddDays(-_retentionDays);
                    var deleted = await db.PlayerDailyStats.Where(d => d.StatDate < cutoff).ExecuteDeleteAsync(stoppingToken);
                    if (deleted > 0)
                        _logger.LogInformation("Pruned {Count} PlayerDailyStat rows older than {Cutoff:yyyy-MM-dd}.", deleted, cutoff);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "PlayerDailyStat prune failed."); }

                try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
