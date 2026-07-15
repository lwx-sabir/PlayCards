using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Games.ThreeCardPoker
{
    /// <summary>
    /// Background round-driver: every couple of seconds it ticks each active 3CP table so a round finishes on its
    /// own — auto-folding any seat whose decision timer expired, then revealing + settling. Without it a table
    /// whose player never decides would hang. Each tick takes the per-table lock, so it never races a live action.
    /// </summary>
    public sealed class ThreeCardPokerRoundDriver : BackgroundService
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
        private readonly ThreeCardPokerTableManager _tables;
        private readonly ILogger<ThreeCardPokerRoundDriver> _logger;

        public ThreeCardPokerRoundDriver(ThreeCardPokerTableManager tables, ILogger<ThreeCardPokerRoundDriver> logger)
        {
            _tables = tables; _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            long tick = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var ids = await _tables.GetActiveTableIdsAsync();
                    foreach (var id in ids)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        try { await _tables.TickTableAsync(id); }
                        catch (Exception ex) { _logger.LogWarning(ex, "3CP round-driver tick failed for table {TableId}", id); }
                    }

                    // Low-cadence stranded-stake reconciliation (~every 2 min at a 2s tick). No-op unless
                    // Reconciliation:Enabled — the heavy DB scan never runs in prod until explicitly opted in.
                    if (tick % 60 == 0)
                    {
                        try { await _tables.ReconcileStrandedStakesAsync(); }
                        catch (Exception ex) { _logger.LogWarning(ex, "3CP reconciliation sweep failed"); }
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "3CP round-driver loop error"); }

                tick++;
                try { await Task.Delay(TickInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
