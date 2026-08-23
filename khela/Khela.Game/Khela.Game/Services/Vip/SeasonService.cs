using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Leaderboards;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Vip
{
    public interface ISeasonService
    {
        /// <summary>The open season, creating the first one (and seeding everyone's SP) if the system has never run. Cached ~30 s.</summary>
        Task<Season> CurrentAsync();

        /// <summary>Roll the open season if it has ended: reset every player to their tier's <c>ResetTo</c> and their SP to
        /// that tier's bar, close it, open the next. Idempotent and resumable. Returns how many players were reset.</summary>
        Task<int> RollIfDueAsync(CancellationToken ct = default);

        /// <summary>Force a roll now (admin), whatever the clock says.</summary>
        Task<int> RollNowAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Status seasons (docs/VIP_SPEC.md §2). SP accrues into the <see cref="CurrencyType.Sp"/> wallet all season and the badge
    /// tier is the band that balance reaches; at the roll everyone drops to their tier's <c>ResetTo</c> and their SP is set to
    /// that tier's bar, so the climb is worth making again.
    ///
    /// One fact makes the whole thing simple: within a season SP only ever goes UP — accrual credits, and nothing debits until
    /// the roll. So the band of the current balance IS the tier the player climbed to (no peak to track), mid-season demotion
    /// cannot happen, and the roll is the only place a tier ever falls.
    ///
    /// The reset is a wallet MOVEMENT, not an assignment: it lands in the ledger with a correlation id per (season, player), so
    /// it audits like every other balance change and re-running a roll costs nothing.
    /// </summary>
    public sealed class SeasonService : ISeasonService
    {
        private const string SettingsHashKey = "khela:settings";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
        private static readonly object Gate = new object();
        private static Season _cached;
        private static DateTime _cachedAtUtc;

        private readonly AppDbContext _db;
        private readonly IRedisService _redis;
        private readonly IWalletService _wallet;
        private readonly IConfiguration _config;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<SeasonService> _logger;

        public SeasonService(AppDbContext db, IRedisService redis, IWalletService wallet, IServiceScopeFactory scopes,
            IConfiguration config, ILogger<SeasonService> logger)
        {
            _db = db; _redis = redis; _wallet = wallet; _scopes = scopes; _config = config; _logger = logger;
        }

        /// <summary>Season length in days; 0 (the default until it is switched on) = a LIFETIME season that never rolls.</summary>
        private async Task<int> LengthDaysAsync()
        {
            int days = _config.GetValue("Season:LengthDays", 0);
            try
            {
                var v = await _redis.GetDatabase().HashGetAsync(SettingsHashKey, "Season:LengthDays");
                if (v.HasValue && int.TryParse(v, out var overlay)) days = overlay;
            }
            catch { }
            return Math.Max(0, days);
        }

        public async Task<Season> CurrentAsync()
        {
            lock (Gate) { if (_cached != null && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return _cached; }

            var open = await _db.Seasons.AsNoTracking().Where(s => s.Status == SeasonStatus.Open)
                .OrderByDescending(s => s.Index).FirstOrDefaultAsync();
            if (open == null) open = await BootstrapAsync();

            lock (Gate) { _cached = open; _cachedAtUtc = DateTime.UtcNow; }
            return open;
        }

        public static void Invalidate() { lock (Gate) { _cached = null; _cachedAtUtc = default; } }

        /// <summary>
        /// First run ever: open season 1 and SEED every existing player's SP wallet to the bar of the tier they already hold.
        /// That is the migration off the old trailing-window model — it preserves every badge exactly, without pretending a
        /// 12-month trailing sum was ever a season's total. Idempotent per player on the correlation id.
        /// </summary>
        private async Task<Season> BootstrapAsync()
        {
            var season = new Season { Index = 1, StartsAtUtc = DateTime.UtcNow, Status = SeasonStatus.Open };
            var days = await LengthDaysAsync();
            if (days > 0) season.EndsAtUtc = season.StartsAtUtc.AddDays(days);

            _db.Seasons.Add(season);
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                // Another instance opened it first — use theirs.
                _db.ChangeTracker.Clear();
                return await _db.Seasons.AsNoTracking().Where(s => s.Status == SeasonStatus.Open)
                    .OrderByDescending(s => s.Index).FirstAsync();
            }

            var cfg = await EffectiveCfgAsync();
            var holders = await _db.UserProfiles.AsNoTracking().Where(p => p.VipTier > VipTier.None)
                .Select(p => new { p.UserId, p.VipTier }).ToListAsync();
            int seeded = 0;
            foreach (var h in holders)
            {
                var bar = VipMath.SpBar(cfg, (int)h.VipTier);
                if (bar <= 0) continue;
                try
                {
                    // A scope per player: WalletService leaves its transaction open when it throws, so one bad wallet on a
                    // shared context would silently discard every seed after it — and this runs ONCE, so those players would
                    // never be seeded at all and would simply lose the badge they had.
                    using var scope = _scopes.CreateScope();
                    var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
                    await wallet.CreditAsync(h.UserId.ToString(), CurrencyType.Sp, bar, TransactionType.AdminAdjustment,
                        $"spseed:{h.UserId:N}", new WalletContext { Description = "Season 1: seeded from the current tier" });
                    seeded++;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[Season] could not seed SP for {UserId}", h.UserId); }
            }
            if (seeded > 0) _logger.LogInformation("[Season] opened season 1 and seeded SP for {Count} tier holder(s).", seeded);
            return season;
        }

        public async Task<int> RollIfDueAsync(CancellationToken ct = default)
        {
            var season = await CurrentAsync();

            // Turning seasons ON has to reach the season that is already open. Seasons ship OFF (length 0 ⇒ the first
            // season is a lifetime one with no end), so without this the knob would be permanently inert: the open season
            // never ends, so it never rolls, so the next one — the one that would have picked up the new length — is never
            // created. The clock starts when it is switched on, never retroactively.
            var days = await LengthDaysAsync();
            if (season.EndsAtUtc == null && days > 0)
            {
                var ends = DateTime.UtcNow.AddDays(days);
                var rows = await _db.Seasons.Where(s => s.Id == season.Id && s.EndsAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndsAtUtc, ends), ct);
                if (rows > 0)
                {
                    Invalidate();
                    _logger.LogInformation("[Season] seasons switched ON: season {Index} now ends {End:u}.", season.Index, ends);
                }
                return 0;
            }

            if (season.EndsAtUtc == null || DateTime.UtcNow < season.EndsAtUtc.Value) return 0;   // lifetime, or not due
            return await RollAsync(season, ct);
        }

        public async Task<int> RollNowAsync(CancellationToken ct = default) => await RollAsync(await CurrentAsync(), ct);

        private async Task<int> RollAsync(Season season, CancellationToken ct)
        {
            // One roller. The lease expires so an interrupted roll resumes rather than stranding the season open forever.
            var lease = $"khela:season:roll:{season.Index}";
            try
            {
                if (!await _redis.GetDatabase().StringSetAsync(lease, Environment.MachineName, TimeSpan.FromMinutes(30), StackExchange.Redis.When.NotExists))
                    return 0;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[Season] no Redis lease available; not rolling."); return 0; }

            try
            {
                var cfg = await EffectiveCfgAsync();
                int rolled = 0;

                // Everyone who could have something to reset: a tier above None, or any SP at all.
                var spHolders = await _db.PlayerWallets.AsNoTracking()
                    .Where(w => w.Currency == CurrencyType.Sp && w.Balance > 0m).Select(w => w.UserId).ToListAsync(ct);
                var tierHolders = await _db.UserProfiles.AsNoTracking()
                    .Where(p => p.VipTier > VipTier.None).Select(p => p.UserId).ToListAsync(ct);
                var players = spHolders.Union(tierHolders).Distinct().ToList();

                foreach (var userId in players)
                {
                    if (ct.IsCancellationRequested) break;
                    // A scope — and so a DbContext — per PLAYER. WalletService leaves its transaction open when it throws,
                    // and a poisoned context would silently enlist (and then discard) every player rolled after it. Throwing
                    // the context away with the row that poisoned it is the only way one bad player can't cost the rest
                    // their reset. (Same defect the LP migration's review found; the shape is identical.)
                    using var scope = _scopes.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<ISeasonService>() as SeasonService;
                    try { if (svc != null && await svc.RollOneAsync(userId, season, cfg, ct)) rolled++; }
                    catch (Exception ex) { _logger.LogError(ex, "[Season] roll failed for {UserId}; the next pass retries.", userId); }
                }

                // Close this season and open the next — LAST, so a crash mid-roll leaves the season open and resumable.
                var open = await _db.Seasons.FirstOrDefaultAsync(s => s.Id == season.Id, ct);
                if (open != null && open.Status == SeasonStatus.Open)
                {
                    var now = DateTime.UtcNow;
                    open.Status = SeasonStatus.Closed;
                    open.RolledAtUtc = now;
                    open.PlayersRolled = rolled;

                    var days = await LengthDaysAsync();
                    var next = new Season
                    {
                        Index = open.Index + 1,
                        StartsAtUtc = open.EndsAtUtc ?? now,
                        EndsAtUtc = days > 0 ? (open.EndsAtUtc ?? now).AddDays(days) : (DateTime?)null,
                        Status = SeasonStatus.Open,
                    };
                    // A season that ended while the server was down must not open the next one already expired.
                    while (next.EndsAtUtc.HasValue && next.EndsAtUtc.Value <= now) next.EndsAtUtc = next.EndsAtUtc.Value.AddDays(days);
                    _db.Seasons.Add(next);
                    await _db.SaveChangesAsync(ct);
                    Invalidate();
                    _logger.LogInformation("[Season] season {Index} rolled: {Rolled} player(s) reset; season {Next} runs to {End}.",
                        open.Index, rolled, next.Index, next.EndsAtUtc?.ToString("u") ?? "forever");
                }
                return rolled;
            }
            finally
            {
                try { await _redis.GetDatabase().KeyDeleteAsync(lease); } catch { }
            }
        }

        /// <summary>Reset one player. Returns true if anything moved. Idempotent on the (season, player) correlation id.</summary>
        internal async Task<bool> RollOneAsync(Guid userId, Season season, VipConfig cfg, CancellationToken ct)
        {
            var corr = $"spsn:{season.Index}:{userId:N}";

            // ALREADY ROLLED? The wallet is idempotent on the correlation id, but the TIER assignment is not — and a roll
            // is designed to be resumed (a crash mid-loop leaves the season open). Re-deriving from an already-reset
            // balance would band to the reset tier and drop them a rung AGAIN, every resume, with no ledger row to show
            // for it. The money's own key is therefore the gate for the whole per-player reset.
            if (await _db.WalletTransactions.AsNoTracking().AnyAsync(t => t.CorrelationId == corr, ct))
                return false;

            var balance = await _wallet.GetBalanceAsync(userId.ToString(), CurrencyType.Sp);
            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
            if (profile == null) return false;

            // The band of the balance IS the tier climbed to — SP only goes up within a season. The player's real spend is
            // used, not an infinity: with a floor re-imposed, pretending they had spent everything would let the ROLL
            // promote someone who never qualified for that tier all season.
            var spend = await TrailingSpendAsync(userId, cfg, DateTime.UtcNow, ct);
            var climbed = (VipTier)Math.Max((int)profile.VipTier, (int)VipMath.ResolveBand((long)balance, spend, profile.Level, cfg));
            var resetTo = (VipTier)Math.Clamp(VipMath.ResetTo(cfg, (int)climbed), 0, (int)climbed);
            var target = (decimal)VipMath.SpBar(cfg, (int)resetTo);
            var delta = target - balance;

            // The reset is a ledger MOVEMENT, so it audits — and its correlation id is what makes the whole reset idempotent.
            if (delta < 0m)
                await _wallet.DebitAsync(userId.ToString(), CurrencyType.Sp, -delta, TransactionType.AdminAdjustment, corr,
                    new WalletContext { Description = $"Season {season.Index} reset → {resetTo}" });
            else
                // Zero moves nothing but still writes the row, so the gate above sees this player as rolled next time.
                await _wallet.CreditAsync(userId.ToString(), CurrencyType.Sp, delta, TransactionType.AdminAdjustment, corr,
                    new WalletContext { Description = $"Season {season.Index} reset → {resetTo}" });

            if (profile.VipTier != resetTo)
            {
                profile.VipTier = resetTo;
                profile.UpdatedAt = DateTime.UtcNow;
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }
            }
            return true;
        }

        /// <summary>The player's trailing USD spend — only queried when some tier still has a floor (they ship at 0).</summary>
        private async Task<decimal> TrailingSpendAsync(Guid userId, VipConfig cfg, DateTime now, CancellationToken ct)
        {
            bool anyFloor = false;
            if (cfg.SpendFloorsUsd != null)
                foreach (var f in cfg.SpendFloorsUsd) if (f > 0m) { anyFloor = true; break; }
            if (!anyFloor) return decimal.MaxValue;

            var windowStart = now.AddMonths(-Math.Max(1, cfg.TierWindowMonths));
            return await _db.StorePurchases.AsNoTracking()
                .Where(s => s.UserId == userId && !s.IsTest && s.CreatedAt >= windowStart
                         && (s.Status == StorePurchaseStatus.Granted || s.Status == StorePurchaseStatus.Refunded))
                .SumAsync(s => (decimal?)s.UsdReference, ct) ?? 0m;
        }

        private async Task<VipConfig> EffectiveCfgAsync()
        {
            var b = new VipConfig();
            try
            {
                var entries = await _redis.GetDatabase().HashGetAllAsync(SettingsHashKey);
                if (entries == null || entries.Length == 0) return b;
                var map = new Dictionary<string, string>(entries.Length);
                foreach (var e in entries) map[(string)e.Name] = (string)e.Value;
                return VipConfig.Overlay(b, map);
            }
            catch { return b; }
        }
    }
}
