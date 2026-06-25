using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Managers
{
    /// <summary>Per-pass tally for logging / metrics / the admin endpoint.</summary>
    public sealed class ReconciliationSummary
    {
        public int CreditsHealed { get; set; }
        public int Refunds { get; set; }
        public int StillStuck { get; set; }
        public int MismatchesPending { get; set; }
    }

    /// <summary>
    /// Background sweeper that idempotently heals stranded settlements (Part B). A stake can be debited but
    /// its settle credit fail after retries (locked wallet / outage / process death mid-settle); the settle
    /// path records that as a <c>settle_failed</c> audit row but never completes the missing credit. At low
    /// prototype volume that's hand-fixable; under real traffic it accumulates as players debited-but-unpaid.
    ///
    /// <b>OFF by default</b> (<c>Reconciliation:Enabled</c>, default false) — it's an autonomous job that
    /// mutates the money ledger every minute, so it stays a no-op until validated in staging, and the flag is
    /// a permanent kill-switch. Keeping it off in early dev also keeps settle failures VISIBLE so the root
    /// cause gets fixed instead of being quietly papered over.
    ///
    /// All heals reuse the EXACT settle correlation ids (<c>bjr:{roundId}:{seat}:pay</c>) so a duplicate is a
    /// safe no-op via the wallet's unique (WalletId, CorrelationId) index. Refunds use a distinct
    /// <c>:reconrf</c> id. Chips only (dual-currency guard intact).
    /// </summary>
    public sealed class SettlementReconciliationService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _config;
        private readonly BlackjackTableManager _tables;
        private readonly IRedisService _redis;
        private readonly ILogger<SettlementReconciliationService> _logger;

        public SettlementReconciliationService(IServiceScopeFactory scopes, IConfiguration config,
            BlackjackTableManager tables, IRedisService redis, ILogger<SettlementReconciliationService> logger)
        {
            _scopes = scopes;
            _config = config;
            _tables = tables;
            _redis = redis;
            _logger = logger;
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            // Loud at startup so enabling the autonomous money-healer in staging/prod is never a surprise.
            var enabled = _config.GetValue("Reconciliation:Enabled", false);
            var interval = Math.Max(10, _config.GetValue("Reconciliation:IntervalSeconds", 60));
            if (enabled)
                _logger.LogWarning("SettlementReconciliationService ENABLED — autonomously healing stranded settlements every {Interval}s. Disable via Reconciliation:Enabled=false.", interval);
            else
                _logger.LogInformation("SettlementReconciliationService started in no-op mode (Reconciliation:Enabled=false). POST /api/reconciliation/run triggers a one-off pass for debugging.");
            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Kill-switch: fully inert unless explicitly enabled (staging/prod only).
                    if (_config.GetValue("Reconciliation:Enabled", false))
                    {
                        var summary = await RunPassAsync(stoppingToken);
                        if (summary.CreditsHealed + summary.Refunds + summary.StillStuck + summary.MismatchesPending > 0)
                            _logger.LogInformation(
                                "Reconciliation pass: {Healed} credits healed, {Refunds} refunds, {Stuck} still-stuck, {Mismatch} mismatches pending review.",
                                summary.CreditsHealed, summary.Refunds, summary.StillStuck, summary.MismatchesPending);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reconciliation pass error");
                }

                var seconds = Math.Max(10, _config.GetValue("Reconciliation:IntervalSeconds", 60));
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// One reconciliation pass. Public so it can be unit/integration-tested directly (and is safe to call
        /// ad-hoc): every mutation is idempotent on its correlation id.
        /// </summary>
        public async Task<ReconciliationSummary> RunPassAsync(CancellationToken ct = default)
        {
            var summary = new ReconciliationSummary();
            var timeoutSeconds = Math.Max(30, _config.GetValue("Reconciliation:RoundSettleTimeoutSeconds", 120));
            var cutoff = DateTime.UtcNow.AddSeconds(-timeoutSeconds);

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();

            // (1) Owed-but-unpaid: a credit failed at settle, leaving a stranded seat. Those are EXACTLY the
            // settle_failed rows (a won seat records its WalletCreditTxId; a losing seat owes 0) — so scan
            // those, not every settled row (keeps the sweep bounded + lets Resolved retire healed rows).
            // settle_failed rows carry Payout = the owed gross. Re-credit via the SAME :pay id (idempotent).
            var rows = await (from p in db.GameHandParticipants
                              join h in db.GameHandHeaders on p.HandId equals h.HandId
                              where !p.Resolved && p.Outcome == "settle_failed"
                                    && h.SettledAt != null && h.SettledAt < cutoff
                              select new { p, h.RoundId, h.TableId })
                             .Take(2000).ToListAsync(ct);

            foreach (var seatGroup in rows.GroupBy(x => new { x.p.HandId, x.p.SeatNumber }))
            {
                if (ct.IsCancellationRequested) break;

                var owed = seatGroup.Sum(x => x.p.Payout);
                if (owed <= 0m) continue; // already-healed rows are filtered by !Resolved, so no alreadyCredited check needed

                var any = seatGroup.First();
                var roundId = any.RoundId;
                var seat = any.p.SeatNumber;
                var userId = any.p.UserId.ToString();
                try
                {
                    // Mirror settle's proportional payout: credit back the stake's gifted fraction so a
                    // reconciled win keeps its taint (no laundering). Read the seat's Bet ledger for the round.
                    var stakeRows = await db.WalletTransactions.AsNoTracking()
                        .Where(t => t.Type == TransactionType.Bet && t.RoundId == roundId
                                    && t.CorrelationId != null && t.CorrelationId.StartsWith($"bjr:{roundId}:{seat}:"))
                        .Select(t => new { t.Amount, t.GiftedDelta }).ToListAsync(ct);
                    var totalStaked = stakeRows.Sum(t => -t.Amount);
                    var giftedStaked = stakeRows.Sum(t => -t.GiftedDelta);
                    var giftedCredit = totalStaked > 0m ? Math.Round(owed * (giftedStaked / totalStaked), 4) : 0m;
                    var cx = new WalletContext { TableId = any.TableId, RoundId = roundId, Description = $"Reconcile payout round {roundId} seat {seat}", CreditGiftedAmount = giftedCredit };
                    // Same :pay id as settle → a duplicate is a safe no-op (wallet's unique correlation index).
                    var txn = await wallet.CreditAsync(userId, CurrencyType.Chips, owed, TransactionType.Win, $"bjr:{roundId}:{seat}:pay", cx);
                    var txIdStr = txn.TransactionId.ToString();
                    foreach (var x in seatGroup)
                    {
                        x.p.Resolved = true;
                        x.p.WalletCreditTxId = txIdStr;             // stamp every row in the group for a complete audit trail
                        db.Entry(x.p).State = EntityState.Modified;  // explicit — persist even if the projection didn't track p
                    }
                    summary.CreditsHealed++;
                    _logger.LogWarning("Reconciled missing payout: round {RoundId} seat {Seat} credited {Owed}.", roundId, seat, owed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reconcile credit failed round {RoundId} seat {Seat}", roundId, seat);
                    summary.StillStuck++;
                }
            }
            await db.SaveChangesAsync(ct);

            // (2) Orphan stakes: a stake debit (Bet) for a round that NEVER produced a settled header and was
            // never paid/refunded → the round died before settling, so refund the staked amount via a distinct
            // :reconrf id (idempotent). Uses a SEPARATE, generous quiet window (default 10 min, far longer than
            // any live round's turn-timer lifetime) AND a per-round liveness check against the table's current
            // round — so a slow-but-still-alive round is never refunded out from under an imminent settle
            // (which would double-pay). This path only fires for genuinely dead rounds.
            var orphanQuietSeconds = Math.Max(timeoutSeconds, _config.GetValue("Reconciliation:OrphanRefundQuietSeconds", 600));
            var orphanCutoff = DateTime.UtcNow.AddSeconds(-orphanQuietSeconds);
            var staleStakes = await db.WalletTransactions
                .Where(t => t.Type == TransactionType.Bet && t.RoundId != null && t.CreatedAt < orphanCutoff
                            && t.CorrelationId != null && t.CorrelationId.StartsWith("bjr:"))
                .Take(2000).ToListAsync(ct);

            foreach (var roundGroup in staleStakes.GroupBy(t => t.RoundId))
            {
                if (ct.IsCancellationRequested) break;
                var roundId = roundGroup.Key;

                if (await db.GameHandHeaders.AnyAsync(h => h.RoundId == roundId, ct)) continue; // settled → handled above
                if (await db.WalletTransactions.AnyAsync(
                        t => t.RoundId == roundId && (t.Type == TransactionType.Win || t.Type == TransactionType.Refund), ct))
                    continue; // already paid or refunded

                // Never refund a round that is settling or already settled — settle commits credits before its
                // header persists, so this closes the window where an orphan-refund would double-pay.
                var rdb = _redis.GetDatabase();
                if (await rdb.KeyExistsAsync($"bjr:settling:{roundId}") || await rdb.KeyExistsAsync($"bjr:settled:{roundId}"))
                    continue;

                // Liveness FAIL-SAFE: if we can't confirm the round's table is dead — TableId unknown, or the
                // table is still on this round — DON'T refund (refunding a live round double-pays at settle).
                var tableId = roundGroup.First().TableId;
                if (string.IsNullOrEmpty(tableId)) continue;
                var liveTable = await _tables.GetTableAsync(tableId);
                if (liveTable != null && liveTable.CurrentRoundId == roundId) continue;

                foreach (var seatTxns in roundGroup.GroupBy(t => SeatFromCorrelation(t.CorrelationId)))
                {
                    var seat = seatTxns.Key;
                    if (seat < 0) continue;
                    var staked = seatTxns.Sum(t => -t.Amount); // Bet amounts are signed negative
                    if (staked <= 0m) continue;

                    var walletId = seatTxns.First().WalletId;
                    var pw = await db.PlayerWallets.FirstOrDefaultAsync(w => w.WalletId == walletId, ct);
                    if (pw == null) continue;
                    var giftedStaked = seatTxns.Sum(t => -t.GiftedDelta);   // restore the exact tainted slice that was debited
                    try
                    {
                        var cx = new WalletContext { TableId = seatTxns.First().TableId, RoundId = roundId, Description = $"Reconcile refund round {roundId} seat {seat}", CreditGiftedAmount = giftedStaked };
                        await wallet.CreditAsync(pw.UserId.ToString(), CurrencyType.Chips, staked, TransactionType.Refund, $"bjr:{roundId}:{seat}:reconrf", cx);
                        summary.Refunds++;
                        _logger.LogWarning("Reconciled orphan stake: round {RoundId} seat {Seat} refunded {Staked}.", roundId, seat, staked);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Reconcile refund failed round {RoundId} seat {Seat}", roundId, seat);
                        summary.StillStuck++;
                    }
                }
            }

            // (3) settle_mismatch rows are NOT auto-resolved — those seats were already PAID the correct rule
            // value; the row is an engine-drift tripwire for humans. Surface the count; leave for ops review.
            summary.MismatchesPending = await db.GameHandParticipants
                .CountAsync(p => p.Outcome == "settle_mismatch" && !p.Resolved, ct);

            return summary;
        }

        // Parses the seat from a "bjr:{roundId}:{seat}:{suffix}" correlation id; -1 if it can't.
        private static int SeatFromCorrelation(string correlationId)
        {
            if (string.IsNullOrEmpty(correlationId)) return -1;
            var parts = correlationId.Split(':');
            return parts.Length >= 4 && int.TryParse(parts[2], out var seat) ? seat : -1;
        }
    }
}
