using System.Globalization;
using Khela.Game.Database;
using Khela.Game.Services.Daily;
using Khela.Game.Services.Piggy;
using Khela.Game.Services.Rewards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// QA tools: put one player's reward ladders back to a known state, without SQL.
    ///
    /// These features are slow to test honestly — a daily ladder takes 28 real days, a pass a month, a piggy several
    /// sessions — so the only way to exercise them is to rewind. Doing that by hand is where the mistakes live: two of
    /// the three resets below have a trap that fails SILENTLY, and both cost real debugging time before they were
    /// understood. They are encoded here so nobody has to remember them again.
    ///
    /// Gated on <c>Testing:Enabled</c> as well as the Admin policy. It deletes claim history and, for the pass, ledger
    /// rows — that is fine on a QA box and is not something to leave one config mistake away from a live one.
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class TestingController : Controller
    {
        private const string SettingsHashKey = "khela:settings";

        private readonly AppDbContext _db;
        private readonly IConnectionMultiplexer _redis;
        private readonly IConfiguration _config;

        public TestingController(AppDbContext db, IConnectionMultiplexer redis, IConfiguration config)
        {
            _db = db; _redis = redis; _config = config;
        }

        private bool ToolsEnabled => _config.GetValue("Testing:Enabled", false);

        [HttpGet]
        public async Task<IActionResult> Index(string q)
        {
            var vm = new TestingVm
            {
                Enabled = ToolsEnabled,
                Query = q,
                Saved = TempData["Saved"] as string,
                Error = TempData["Error"] as string,
            };

            ReadSwitches(vm);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var profile = await FindAsync(q.Trim());
                if (profile == null)
                {
                    vm.Error ??= $"No player matches '{q}'.";
                }
                else
                {
                    vm.UserId = profile.UserId;
                    vm.DisplayName = profile.DisplayName;
                    vm.PublicId = profile.PublicId;
                    vm.Level = profile.Level;
                    await LoadStateAsync(vm);
                }
            }

            return View(vm);
        }

        // ---------------- daily ----------------

        /// <summary>
        /// Rewind the daily ladder so <c>openDays</c> days are collectable, right now.
        ///
        /// Two things happen, and the second one is the whole reason this button exists:
        ///
        /// The anchor moves back so today lands on day N — the ladder only ever offers ONE free day (today's), with
        /// everything before it a missed day, so "open 10 days" means "make today day 10 and leave 1–9 unclaimed".
        /// Those earlier days are collectable only while the ad bypass is on, which is the switch below.
        ///
        /// And the CYCLE INDEX is bumped. Reward payouts are idempotent on a key that embeds the cycle
        /// (<c>daily:d3:{user}:{node}</c>), and the wallet remembers those keys FOREVER — long after the claim rows are
        /// deleted. Clearing claims without a new cycle therefore produces the worst kind of bug: every day re-claims
        /// successfully, the rows say Granted, and not one chip moves. A new cycle means new keys, so the money is real.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetDaily(Guid userId, int openDays)
        {
            if (!ToolsEnabled) return Refuse();

            try
            {
                int days = Math.Clamp(openDays <= 0 ? 1 : openDays, 1, LadderLength());

                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM PlayerDailyClaims WHERE UserId = {userId}");
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM PlayerDailyAdUnlocks WHERE UserId = {userId}");

                var start = DateTime.UtcNow.Date.AddDays(-(days - 1));
                var rows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE PlayerDailyCycles
                       SET CycleIndex = CycleIndex + 1, StartLocalDate = {start}, UpdatedAt = UTC_TIMESTAMP()
                     WHERE UserId = {userId}");

                TempData["Saved"] = rows > 0
                    ? $"Daily reset: today is now day {days} of {LadderLength()}, {days} day(s) collectable, cycle bumped so payouts are fresh."
                    : "Daily reset: claims cleared. This player has no cycle row yet — it is created the first time they open the panel, and will start at day 1.";
            }
            catch (Exception ex) { TempData["Error"] = $"Daily reset failed: {ex.Message}"; }

            return Back(userId);
        }

        // ---------------- pass ----------------

        /// <summary>
        /// Wipe this player's pass claims for the CURRENT cycle so the ladder can be collected again.
        ///
        /// The pass has no cycle counter to bump — its cycle key is the calendar month, derived from the date — so the
        /// daily trick is not available. The only way to free the idempotency keys is to delete the ledger rows that
        /// hold them, which is why this is a QA-only button: it leaves gaps in the wallet's audit history for that
        /// player. Balances are stored values and are untouched; what is lost is the ability to reconstruct them.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPass(Guid userId)
        {
            if (!ToolsEnabled) return Refuse();

            try
            {
                var claims = await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM PlayerPassClaims WHERE UserId = {userId}");
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM PlayerPassAdUnlocks WHERE UserId = {userId}");

                // The reward keys embed the user id without dashes: pass:{passKey}:{cycleKey}:{userId:N}:{node}:{line}
                var tag = $"%{userId:N}%";
                var ledger = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    DELETE t FROM WalletTransactions t
                      JOIN PlayerWallets w ON w.WalletId = t.WalletId
                     WHERE w.UserId = {userId}
                       AND (t.CorrelationId LIKE {"pass:" + tag} OR t.CorrelationId LIKE {"pass-retro:" + tag})");

                TempData["Saved"] = $"Pass reset: {claims} claim(s) cleared and {ledger} reward ledger row(s) deleted so re-claims pay again.";
            }
            catch (Exception ex) { TempData["Error"] = $"Pass reset failed: {ex.Message}"; }

            return Back(userId);
        }

        // ---------------- piggy ----------------

        /// <summary>
        /// <c>fill</c> puts the bank at capacity so the ready state and its countdown can be seen without days of
        /// wagering; <c>clear</c> empties it; <c>uncap</c> clears today's accrual so the daily cap stops blocking a
        /// test session. The cap is the single most common reason a piggy "stops filling for no reason".
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Piggy(Guid userId, string mode)
        {
            if (!ToolsEnabled) return Refuse();

            try
            {
                var bank = await _db.PlayerPiggyBanks.FirstOrDefaultAsync(p => p.UserId == userId);
                if (bank == null)
                {
                    TempData["Error"] = "No piggy bank yet — it is created on the player's first settled wager.";
                    return Back(userId);
                }

                // Re-read the ladder for this player's level FIRST, so fill/clear act on the capacity they should
                // have now rather than whatever the bank was opened with. Without this, "fill to full" fills an old
                // 250k bank for a player who has long since earned a bigger one.
                var level = await _db.UserProfiles.AsNoTracking()
                    .Where(p => p.UserId == userId).Select(p => p.Level).FirstOrDefaultAsync();

                var cfg = EffectivePiggy();
                var (tier, max, _) = Khela.Game.Services.Piggy.PiggyMath.TierFor(level, cfg);
                if (max > 0m && max != bank.MaxAmount)
                {
                    bank.Tier = tier;
                    bank.MaxAmount = max;
                }

                switch ((mode ?? "").ToLowerInvariant())
                {
                    case "fill":
                        bank.Amount = bank.MaxAmount;
                        bank.ReadyAtUtc = DateTime.UtcNow;
                        // Deliberately NOT seen: the countdown must start when the player is actually shown the full
                        // bank, which is the behaviour under test.
                        bank.SeenAtUtc = null;
                        bank.ExpiresAtUtc = null;
                        // Owed a celebration. Without this the fill can land on an already-celebrated level, leaving
                        // nothing unseen — the bank looks full and the chips never fly, which is not what the button
                        // says it does.
                        bank.CelebratedAmount = 0m;
                        TempData["Saved"] = "Piggy filled to capacity, with the whole amount owed a celebration. Open the game: the chips should fly in, the ready state should appear, and the countdown should start on that first sighting.";
                        break;

                    case "clear":
                        bank.Amount = 0m;
                        bank.CelebratedAmount = 0m;   // a fresh bank owes nothing and starts its own story
                        bank.ReadyAtUtc = null;
                        bank.SeenAtUtc = null;
                        bank.ExpiresAtUtc = null;
                        bank.AccruedToday = 0m;
                        bank.AccrualDateUtc = null;
                        TempData["Saved"] = "Piggy emptied and its window cleared.";
                        break;

                    case "uncap":
                        bank.AccruedToday = 0m;
                        bank.AccrualDateUtc = null;
                        TempData["Saved"] = "Today's accrual cap cleared — the bank will take chips again without changing the configured cap.";
                        break;

                    default:
                        TempData["Error"] = $"Unknown piggy action '{mode}'.";
                        return Back(userId);
                }

                bank.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
            catch (Exception ex) { TempData["Error"] = $"Piggy action failed: {ex.Message}"; }

            return Back(userId);
        }

        // ---------------- switches ----------------

        /// <summary>
        /// Flip a testing switch, live. These write the Redis settings hash rather than appsettings, so they take
        /// effect on the very next claim with no restart — which is the only thing that makes them usable mid-test.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Switches(string userId)
        {
            if (!ToolsEnabled) return Refuse();

            try
            {
                bool bypassAds = Request.Form["bypassAds"].Count > 0;
                bool bypassPurchase = Request.Form["bypassPurchase"].Count > 0;

                await _redis.GetDatabase().HashSetAsync(SettingsHashKey, new[]
                {
                    new HashEntry(RewardSwitches.BypassAdField, bypassAds ? "true" : "false"),
                    new HashEntry("Piggy:BypassPurchase", bypassPurchase ? "true" : "false"),
                });

                TempData["Saved"] = $"Switches saved — live on the next claim. Missed days free: {(bypassAds ? "YES" : "no")}. " +
                                    $"Piggy break without purchase: {(bypassPurchase ? "YES" : "no")}.";
            }
            catch (Exception ex) { TempData["Error"] = $"Could not save switches: {ex.Message}"; }

            return RedirectToAction(nameof(Index), new { q = userId });
        }

        // ---------------- helpers ----------------

        private IActionResult Refuse()
        {
            TempData["Error"] = "Testing tools are disabled. Set Testing:Enabled = true in appsettings on THIS environment.";
            return RedirectToAction(nameof(Index));
        }

        private IActionResult Back(Guid userId) => RedirectToAction(nameof(Index), new { q = userId.ToString() });

        private async Task<Khela.Game.Database.Models.UserProfile> FindAsync(string q)
        {
            if (Guid.TryParse(q, out var id))
                return await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == id);

            return await _db.UserProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PublicId == q || p.DisplayName == q)
                ?? await _db.UserProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.DisplayName.Contains(q));
        }

        /// <summary>The piggy config the API is actually running: the Redis overlay on top of the built-in defaults.</summary>
        private Khela.Game.Services.Piggy.PiggyConfig EffectivePiggy()
        {
            var baseCfg = new Khela.Game.Services.Piggy.PiggyConfig();
            try
            {
                var entries = _redis.GetDatabase().HashGetAll(SettingsHashKey);
                if (entries == null || entries.Length == 0) return baseCfg;

                var map = new Dictionary<string, string>(entries.Length);
                foreach (var e in entries) map[(string)e.Name] = (string)e.Value;
                return Khela.Game.Services.Piggy.PiggyConfig.Overlay(baseCfg, map);
            }
            catch { return baseCfg; }
        }

        /// <summary>Ladder length from the live config if one was authored, else the built-in default.</summary>
        private int LadderLength()
        {
            try
            {
                var json = _redis.GetDatabase().StringGet(DailyCatalog.RedisKey);
                if (json.HasValue)
                {
                    var cfg = DailyCatalog.Parse(json, out _);
                    if (cfg != null && cfg.Days > 0) return cfg.Days;
                }
            }
            catch { }
            return DailyCatalog.Defaults().Days;
        }

        private void ReadSwitches(TestingVm vm)
        {
            vm.LadderDays = LadderLength();
            try
            {
                var db = _redis.GetDatabase();

                var ads = db.HashGet(SettingsHashKey, RewardSwitches.BypassAdField);
                vm.BypassAds = ads.HasValue
                    ? ads == "true"
                    : _config.GetValue("Rewards:BypassAdForMissedDays", false);
                vm.BypassAdsFromRedis = ads.HasValue;

                var purchase = db.HashGet(SettingsHashKey, "Piggy:BypassPurchase");
                vm.BypassPurchase = purchase.HasValue
                    ? purchase == "true"
                    : _config.GetValue("Piggy:BypassPurchase", false);
            }
            catch { vm.Error ??= "Could not reach Redis — switch states below may be stale."; }
        }

        private async Task LoadStateAsync(TestingVm vm)
        {
            var cycle = await _db.PlayerDailyCycles.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == vm.UserId);
            if (cycle != null)
            {
                vm.DailyCycle = $"d{cycle.CycleIndex}";
                vm.DailyDayIndex = (int)(DateTime.UtcNow.Date - cycle.StartLocalDate.Date).TotalDays + 1;
                vm.DailyStart = cycle.StartLocalDate;
            }

            vm.DailyClaims = await _db.PlayerDailyClaims.CountAsync(c => c.UserId == vm.UserId);
            vm.PassClaims = await _db.PlayerPassClaims.CountAsync(c => c.UserId == vm.UserId);

            var bank = await _db.PlayerPiggyBanks.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == vm.UserId);
            if (bank != null)
            {
                vm.HasPiggy = true;
                vm.PiggyAmount = bank.Amount;
                vm.PiggyMax = bank.MaxAmount;
                vm.PiggyTier = bank.Tier;
                vm.PiggyAccruedToday = bank.AccruedToday;
                vm.PiggySeenAtUtc = bank.SeenAtUtc;
                vm.PiggyExpiresAtUtc = bank.ExpiresAtUtc;
                vm.PiggyExpiredCount = bank.ExpiredCount;
            }
        }
    }

    public sealed class TestingVm
    {
        public bool Enabled { get; set; }
        public string Query { get; set; }
        public string Saved { get; set; }
        public string Error { get; set; }

        public Guid UserId { get; set; }
        public string DisplayName { get; set; }
        public string PublicId { get; set; }
        public int Level { get; set; }
        public bool Found => UserId != Guid.Empty;

        public int LadderDays { get; set; } = 28;
        public string DailyCycle { get; set; }
        public int DailyDayIndex { get; set; }
        public DateTime? DailyStart { get; set; }
        public int DailyClaims { get; set; }
        public int PassClaims { get; set; }

        public bool HasPiggy { get; set; }
        public decimal PiggyAmount { get; set; }
        public decimal PiggyMax { get; set; }
        public int PiggyTier { get; set; }
        public decimal PiggyAccruedToday { get; set; }
        public DateTime? PiggySeenAtUtc { get; set; }
        public DateTime? PiggyExpiresAtUtc { get; set; }
        public int PiggyExpiredCount { get; set; }

        public bool BypassAds { get; set; }
        public bool BypassAdsFromRedis { get; set; }
        public bool BypassPurchase { get; set; }
    }
}
