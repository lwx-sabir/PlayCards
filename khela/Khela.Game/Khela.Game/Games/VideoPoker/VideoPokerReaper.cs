using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Games.VideoPoker
{
    /// <summary>
    /// Background sweeper: every few seconds it resolves video-poker hands that were dealt but never drawn (the player
    /// abandoned the hand after the stake was debited). Once past the stale timeout they auto-settle as STAND PAT, so a
    /// debited bet is always resolved and never stranded. Each settle takes the per-hand lock, so it never races a
    /// player who returns to draw. There is no per-hand table state to keep alive (unlike the 3CP driver) — this exists
    /// purely for money-safety on abandonment.
    /// </summary>
    public sealed class VideoPokerReaper : BackgroundService
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
        private readonly VideoPokerService _service;
        private readonly ILogger<VideoPokerReaper> _logger;

        public VideoPokerReaper(VideoPokerService service, ILogger<VideoPokerReaper> logger)
        {
            _service = service; _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            long tick = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    int resolved = await _service.ResolveStaleHandsAsync();
                    if (resolved > 0) _logger.LogInformation("VP reaper settled {Count} abandoned/stuck hand(s).", resolved);

                    // Low-cadence DB-ledger orphan sweep (~every 2 min at a 15s tick). No-op unless Reconciliation:Enabled —
                    // the heavy WalletTransactions scan never runs in prod until explicitly opted in.
                    if (tick % 8 == 0)
                    {
                        try { await _service.ReconcileStrandedStakesAsync(); }
                        catch (Exception ex) { _logger.LogWarning(ex, "VP reconciliation sweep failed"); }
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "VP reaper loop error"); }

                tick++;
                try { await Task.Delay(TickInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
