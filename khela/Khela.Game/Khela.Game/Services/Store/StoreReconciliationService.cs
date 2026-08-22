using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Pass;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Store.Verification;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Khela.Game.Services.Store
{
    /// <summary>Counts from one reconciliation pass (for logs/tests).</summary>
    public sealed class StoreReconcileSummary
    {
        public int Redriven { get; set; }
        public int Acknowledged { get; set; }
        public int Refunded { get; set; }
        public int SubscriptionsRefreshed { get; set; }
        public int AdminRedrives { get; set; }
        public int Errors { get; set; }
        public int Total => Redriven + Acknowledged + Refunded + SubscriptionsRefreshed + AdminRedrives + Errors;
    }

    /// <summary>
    /// The store's safety net (docs/IAP_SPEC.md §5.5 / §7): every <c>Store:ReconcileIntervalSeconds</c> it
    /// <list type="number">
    /// <item>re-drives rows stuck Pending/Verified/Granted-incomplete (a client that died between redeem and confirm, a transient
    /// store outage) — every leg is idempotent, so re-running is always safe;</item>
    /// <item>acknowledges/consumes Google purchases that were granted but never acknowledged (Google refunds un-acknowledged
    /// purchases after 3 days — the player would keep the chips AND get the money back);</item>
    /// <item>polls Google's voided-purchases list and applies the refund policy (the backstop to RTDN);</item>
    /// <item>refreshes subscription windows about to end (renewal / expiry) so a missed webhook can't leave a dead subscription golden;</item>
    /// <item>executes admin re-drive requests queued in Redis (<c>khela:store:redrive</c>) — the web process never pays.</item>
    /// </list>
    /// Fully inert when the store is off. Same shape as <c>SettlementReconciliationService</c>.
    /// </summary>
    public sealed class StoreReconciliationService : BackgroundService
    {
        public const string RedriveQueueKey = "khela:store:redrive";
        /// <summary>List of "{purchaseId}|{source}|{reason}" — manual refunds/revokes requested from the dashboard.</summary>
        public const string RefundQueueKey = "khela:store:refund";
        public const string VoidedSinceKey = "khela:store:voided:since";
        /// <summary>JSON status the game publishes for the dashboard's Platforms panel (registered verifiers, credentials, last sweeps).</summary>
        public const string StatusKey = "khela:store:status";

        private readonly IServiceScopeFactory _scopes;
        private readonly IOptionsMonitor<StoreOptions> _options;
        private readonly IRedisService _redis;
        private readonly ILogger<StoreReconciliationService> _logger;
        private DateTime _lastVoidedPollUtc = DateTime.MinValue;
        private DateTime _lastSubscriptionSweepUtc = DateTime.MinValue;

        public StoreReconciliationService(IServiceScopeFactory scopes, IOptionsMonitor<StoreOptions> options, IRedisService redis, ILogger<StoreReconciliationService> logger)
        {
            _scopes = scopes; _options = options; _redis = redis; _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the app finish booting before the first pass — but publish the platform status right away so the
            // dashboard's Platforms panel reflects THIS process as soon as it is up.
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { return; }
            try
            {
                using var boot = _scopes.CreateScope();
                await WriteStatusAsync(boot.ServiceProvider.GetRequiredService<StoreVerifierRegistry>(), new StoreReconcileSummary());
            }
            catch (Exception ex) { _logger.LogDebug(ex, "store status publish at boot skipped"); }
            try { await Task.Delay(TimeSpan.FromSeconds(17), stoppingToken); } catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_options.CurrentValue.Enabled)
                    {
                        var s = await RunPassAsync(stoppingToken);
                        if (s.Total > 0)
                            _logger.LogInformation("Store reconcile: {Redriven} re-driven, {Ack} acknowledged, {Refunded} refunded, {Subs} subscriptions refreshed, {Admin} admin re-drives, {Errors} errors.",
                                s.Redriven, s.Acknowledged, s.Refunded, s.SubscriptionsRefreshed, s.AdminRedrives, s.Errors);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Store reconcile pass error"); }

                var seconds = Math.Max(15, _options.CurrentValue.ReconcileIntervalSeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>One pass. Public so it can be exercised directly; every mutation is idempotent.</summary>
        public async Task<StoreReconcileSummary> RunPassAsync(CancellationToken ct = default)
        {
            var summary = new StoreReconcileSummary();
            var o = _options.CurrentValue;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var purchases = scope.ServiceProvider.GetRequiredService<IStorePurchaseService>();
            var verifiers = scope.ServiceProvider.GetRequiredService<StoreVerifierRegistry>();
            var pass = scope.ServiceProvider.GetRequiredService<IPassService>();
            var now = DateTime.UtcNow;

            // 1. re-drive stuck rows
            var cutoff = now.AddSeconds(-Math.Max(30, o.RedriveAfterSeconds));
            var stuck = await db.StorePurchases.AsNoTracking()
                .Where(s => (s.Status == StorePurchaseStatus.Pending || s.Status == StorePurchaseStatus.Verified || (s.Status == StorePurchaseStatus.Granted && s.CompletedAt == null))
                            && s.UpdatedAt <= cutoff && s.Attempts < o.MaxRedriveAttempts)
                .OrderBy(s => s.UpdatedAt).Take(50).Select(s => s.Id).ToListAsync(ct);
            foreach (var id in stuck)
            {
                try { var r = await purchases.RedriveAsync(id, ct); if (r.Ok) summary.Redriven++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { summary.Errors++; _logger.LogWarning(ex, "Re-drive failed for store purchase {Id}", id); }
            }

            // 2. Google acknowledge backstop
            var ackCutoff = now.AddHours(-Math.Max(1, o.GooglePlay.SweepUnacknowledgedAfterHours));
            var unacked = await db.StorePurchases
                .Where(s => s.Platform == StorePlatform.GooglePlay && s.Status == StorePurchaseStatus.Granted && s.AcknowledgedAt == null && s.GrantedAt <= ackCutoff)
                .OrderBy(s => s.GrantedAt).Take(50).ToListAsync(ct);
            if (unacked.Count > 0)
            {
                var google = verifiers.Resolve(StorePlatform.GooglePlay);
                foreach (var row in unacked)
                {
                    try
                    {
                        if (google != null && google.IsConfigured && await google.AcknowledgeAsync(row, ct))
                        {
                            row.AcknowledgedAt = DateTime.UtcNow; row.UpdatedAt = DateTime.UtcNow;
                            await db.SaveChangesAsync(ct); summary.Acknowledged++;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { summary.Errors++; _logger.LogWarning(ex, "Acknowledge sweep failed for {Id}", row.Id); }
                }
            }

            // 3. Google voided-purchases poll
            if (o.GooglePlay.Enabled && now - _lastVoidedPollUtc >= TimeSpan.FromMinutes(Math.Max(5, o.GooglePlay.VoidedPollMinutes)))
            {
                _lastVoidedPollUtc = now;
                var gateway = scope.ServiceProvider.GetService<IGooglePlayGateway>();
                if (gateway != null && gateway.IsConfigured)
                {
                    try
                    {
                        DateTime since = now.AddDays(-7);
                        try
                        {
                            var v = await _redis.GetDatabase().StringGetAsync(VoidedSinceKey);
                            if (v.HasValue && DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)) since = parsed.AddDays(-1);   // overlap a day; idempotent anyway
                        }
                        catch { }
                        var voided = await gateway.ListVoidedPurchasesAsync(since, ct);
                        foreach (var v in voided)
                        {
                            if (string.IsNullOrWhiteSpace(v.PurchaseToken)) continue;
                            var eventId = "voided:" + StoreMath.FitOrHash(v.OrderId ?? v.PurchaseToken, 120);
                            if (await db.StoreEvents.AnyAsync(e => e.Platform == StorePlatform.GooglePlay && e.EventId == eventId, ct)) continue;
                            var row = await db.StorePurchases.AsNoTracking().FirstOrDefaultAsync(s => s.Platform == StorePlatform.GooglePlay && s.StoreTransactionId == v.PurchaseToken, ct);
                            db.StoreEvents.Add(new StoreEvent
                            {
                                Platform = StorePlatform.GooglePlay, EventId = eventId, EventType = "voided",
                                StoreTransactionId = v.PurchaseToken, PurchaseId = row?.Id,
                                RawJson = System.Text.Json.JsonSerializer.Serialize(v), ReceivedAt = DateTime.UtcNow, ProcessedAt = DateTime.UtcNow,
                                Error = row == null ? "no matching purchase row" : null,
                            });
                            await db.SaveChangesAsync(ct);
                            if (row != null && await purchases.MarkRefundedAsync(row.Id, "google-voided", $"voided source {v.VoidedSource} reason {v.VoidedReason}", ct)) summary.Refunded++;
                        }
                        try { await _redis.GetDatabase().StringSetAsync(VoidedSinceKey, now.ToString("O")); } catch { }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { summary.Errors++; _logger.LogWarning(ex, "Voided-purchases poll failed"); }
                }
            }

            // 4. subscription refresh — windows ending within 24 h (or ended within the last 24 h, AutoRenew unknown)
            if (now - _lastSubscriptionSweepUtc >= TimeSpan.FromHours(1))
            {
                _lastSubscriptionSweepUtc = now;
                var horizon = now.AddHours(24);
                var floor = now.AddHours(-48);
                var subs = await db.StorePurchases
                    .Where(s => s.ProductType == StoreProductType.Subscription && s.Status == StorePurchaseStatus.Granted
                                && s.SubscriptionExpiresAt != null && s.SubscriptionExpiresAt <= horizon && s.SubscriptionExpiresAt >= floor)
                    .OrderBy(s => s.SubscriptionExpiresAt).Take(100).ToListAsync(ct);
                foreach (var row in subs)
                {
                    try
                    {
                        var verifier = verifiers.Resolve(row.Platform);
                        if (verifier == null || !verifier.IsConfigured) continue;
                        var fresh = await verifier.RefreshAsync(row, ct);
                        if (fresh == null || fresh.Outcome == VerifyOutcome.Transient) continue;
                        summary.SubscriptionsRefreshed++;
                        // The same seam the webhooks use: renew → new golden window, revoke → refund policy, ended → Expired.
                        var tracked = await db.StorePurchases.FirstOrDefaultAsync(s => s.Id == row.Id, ct) ?? row;
                        var outcome = await purchases.ApplySubscriptionUpdateAsync(tracked, fresh, row.Platform == StorePlatform.AppStore ? "apple-refresh" : "google-refresh", ct);
                        if (outcome == SubscriptionUpdate.Revoked) summary.Refunded++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { summary.Errors++; _logger.LogWarning(ex, "Subscription refresh failed for {Id}", row.Id); }
                }
            }

            // 5. admin queues — the dashboard REQUESTS, the game process EXECUTES (the web app never pays or reverses money itself)
            try
            {
                var redisDb = _redis.GetDatabase();
                for (int i = 0; i < 20; i++)
                {
                    var item = await redisDb.ListLeftPopAsync(RedriveQueueKey);
                    if (!item.HasValue) break;
                    if (!Guid.TryParse(item, out var id)) continue;
                    try { await purchases.RedriveAsync(id, ct); summary.AdminRedrives++; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { summary.Errors++; _logger.LogWarning(ex, "Admin re-drive failed for {Id}", id); }
                }
                for (int i = 0; i < 20; i++)
                {
                    // "{purchaseId}|{source}|{reason}" — a manual refund / revoke from the Purchases page.
                    var item = await redisDb.ListLeftPopAsync(RefundQueueKey);
                    if (!item.HasValue) break;
                    var parts = ((string)item).Split('|', 3);
                    if (parts.Length == 0 || !Guid.TryParse(parts[0], out var id)) continue;
                    var source = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "admin";
                    var reason = parts.Length > 2 ? parts[2] : "manual";
                    try { if (await purchases.MarkRefundedAsync(id, source, reason, ct)) summary.Refunded++; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { summary.Errors++; _logger.LogWarning(ex, "Admin refund failed for {Id}", id); }
                }
            }
            catch (RedisException) { /* Redis down → queues wait */ }

            await WriteStatusAsync(verifiers, summary);
            return summary;
        }

        /// <summary>
        /// What the dashboard shows on its Platforms panel: which verifiers THIS game process has registered and whether
        /// their credentials load, when the sweeps last ran, and the last pass's counts. The web app can't see the
        /// game's appsettings or DI, so the game publishes it. Best-effort; never throws.
        /// </summary>
        private async Task WriteStatusAsync(StoreVerifierRegistry verifiers, StoreReconcileSummary summary)
        {
            try
            {
                var o = _options.CurrentValue;
                var platforms = new System.Collections.Generic.List<object>();
                foreach (var p in new[] { StorePlatform.GooglePlay, StorePlatform.AppStore, StorePlatform.Fake, StorePlatform.Web, StorePlatform.Amazon })
                {
                    var v = verifiers.Resolve(p);
                    platforms.Add(new { platform = p.ToString(), registered = v != null, configured = v?.IsConfigured ?? false });
                }
                var status = new
                {
                    atUtc = DateTime.UtcNow,
                    storeEnabledConfig = o.Enabled,
                    environmentIsDevelopment = verifiers.Resolve(StorePlatform.Fake) != null,
                    googlePackageName = o.GooglePlay.PackageName,
                    googleServiceAccountConfigured = !string.IsNullOrWhiteSpace(o.GooglePlay.ServiceAccountJsonPath),
                    appleBundleId = o.AppStore.BundleId,
                    appleRootCertConfigured = !string.IsNullOrWhiteSpace(o.AppStore.RootCertPath),
                    appleServerApiConfigured = !string.IsNullOrWhiteSpace(o.AppStore.IssuerId) && !string.IsNullOrWhiteSpace(o.AppStore.KeyId) && !string.IsNullOrWhiteSpace(o.AppStore.PrivateKeyPath),
                    refundPolicyConfig = o.Refunds.Policy,
                    reconcileIntervalSeconds = o.ReconcileIntervalSeconds,
                    lastVoidedPollUtc = _lastVoidedPollUtc == DateTime.MinValue ? (DateTime?)null : _lastVoidedPollUtc,
                    lastSubscriptionSweepUtc = _lastSubscriptionSweepUtc == DateTime.MinValue ? (DateTime?)null : _lastSubscriptionSweepUtc,
                    lastPass = new { summary.Redriven, summary.Acknowledged, summary.Refunded, summary.SubscriptionsRefreshed, summary.AdminRedrives, summary.Errors },
                    platforms,
                };
                await _redis.GetDatabase().StringSetAsync(StatusKey, System.Text.Json.JsonSerializer.Serialize(status), TimeSpan.FromHours(24));
            }
            catch (Exception ex) { _logger.LogDebug(ex, "store status write skipped"); }
        }

        private static string PassKeyOf(StorePurchase row)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(row.CatalogSnapshotJson))
                {
                    var p = System.Text.Json.JsonSerializer.Deserialize<StoreProductDef>(row.CatalogSnapshotJson, StoreCatalog.JsonOptions);
                    if (!string.IsNullOrWhiteSpace(p?.Effect?.Arg)) return p.Effect.Arg;
                }
            }
            catch { }
            return PassCatalog.MonthlyKey;
        }
    }
}
