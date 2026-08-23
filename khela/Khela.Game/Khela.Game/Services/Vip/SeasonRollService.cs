using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Vip
{
    /// <summary>
    /// Watches for the open season's end and rolls it (docs/VIP_SPEC.md §2). Also the thing that OPENS season 1 the first
    /// time the server runs, which is what seeds every existing player's SP from the tier they already hold.
    ///
    /// Checks often rather than on a schedule: a season boundary is a wall-clock instant an admin can move by editing
    /// <c>Season:LengthDays</c>, so a cron-shaped job would either miss it or fire on a stale one. <see cref="ISeasonService"/>
    /// does the deciding and holds the lease; this only asks.
    /// </summary>
    public sealed class SeasonRollService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<SeasonRollService> _logger;

        public SeasonRollService(IServiceScopeFactory scopes, ILogger<SeasonRollService> logger)
        {
            _scopes = scopes; _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let start-up settle — and let the LP migration go first, so a fresh deployment does one thing at a time.
            try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var seasons = scope.ServiceProvider.GetRequiredService<ISeasonService>();
                    await seasons.RollIfDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "[Season] roll check failed; retrying next tick."); }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
