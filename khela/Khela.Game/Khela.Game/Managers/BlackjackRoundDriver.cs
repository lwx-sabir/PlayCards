using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Managers
{
    /// <summary>
    /// Background round-driver: every couple of seconds it ticks each active table so a round finishes on
    /// its own — auto-standing a player whose turn timer expired, and dealer-playing + settling once all
    /// hands are resolved. Without it a table whose current player never acts would hang, because the
    /// turn-timeout in EnsureTurn only fires lazily when the NEXT player action arrives.
    /// Every tick takes the per-table lock, so it never races a concurrent player action.
    /// </summary>
    public sealed class BlackjackRoundDriver : BackgroundService
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

        private readonly BlackjackTableManager _tables;
        private readonly ILogger<BlackjackRoundDriver> _logger;

        public BlackjackRoundDriver(BlackjackTableManager tables, ILogger<BlackjackRoundDriver> logger)
        {
            _tables = tables;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var ids = await _tables.GetActiveTableIdsAsync();
                    foreach (var id in ids)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        try { await _tables.TickTableAsync(id); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Round-driver tick failed for table {TableId}", id); }
                    }

                    // Keep each stake tier stocked even when nobody is loading the lobby: tables fill up as people
                    // play, and a tier that has run out of joinable tables must not wait for the next browse to
                    // open more.
                    //
                    // Throttled, not run every tick: the balancer has to look at every table in the lobby, and the
                    // tiers keep three spare joinable tables, so there is nothing urgent about it. Ticking the
                    // TABLES is what has to stay on schedule — turn timers and settlement live there.
                    try { await _tables.BalanceLobbyIfDueAsync(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Lobby balance pass failed"); }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Round-driver loop error");
                }

                try { await Task.Delay(TickInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
