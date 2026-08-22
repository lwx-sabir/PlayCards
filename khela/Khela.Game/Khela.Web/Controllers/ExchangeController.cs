using System.Globalization;
using System.Text;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Config;
using Khela.Game.Services.Exchange;
using Khela.Game.Services.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Currency exchange (docs/EXCHANGE_SPEC.md): the PAIR editor over the Redis document <c>khela:exchange</c> (the same
    /// overlay the game reads; <see cref="ExchangeCatalog.Defaults"/> is the fallback; every save runs the game's own fail-closed
    /// <see cref="ExchangeCatalog.Validate"/> and takes a config backup), the kill switch, and a read-only LEDGER of
    /// <c>CurrencyExchanges</c>. The web process never moves money.
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class ExchangeController : Controller
    {
        private const string SettingsHashKey = "khela:settings";
        private const int PageSize = 50;

        private readonly IConnectionMultiplexer _redis;
        private readonly IConfigBackupService _backups;
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;

        public ExchangeController(IConnectionMultiplexer redis, IConfigBackupService backups, AppDbContext db, IConfiguration config)
        {
            _redis = redis; _backups = backups; _db = db; _config = config;
        }

        // ======================================================================= pairs

        [HttpGet]
        public IActionResult Index()
        {
            var vm = BuildIndex();
            vm.Saved = TempData["Saved"] as string;
            vm.Error = TempData["Error"] as string;
            return View(vm);
        }

        /// <summary>Add a pair (disabled, with a sensible template) and open it in the editor.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(string key)
        {
            var cfg = Effective();
            key = (key ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(key)) return Back("A pair needs a key (e.g. kash_gems).");
            if (cfg.Find(key) != null) return Back($"'{key}' already exists.");
            cfg.Pairs.Add(new ExchangePairDef
            {
                Key = key, Enabled = false, Title = key, SortOrder = (cfg.Pairs.Count + 1) * 10,
                FromCurrency = "Chips", ToCurrency = "Kash", FromPerUnit = 1_000_000m, Step = 1m, MinTo = 1m,
            });
            return SaveConfig(cfg, $"Created '{key}' (disabled — set the currencies and the rate, then enable).", openEdit: key);
        }

        /// <summary>The edit modal's form — one pair, every field.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Save(ExchangePairForm form)
        {
            var cfg = Effective();
            var key = (form.Key ?? "").Trim();
            var p = cfg.Find(key);
            if (p == null) return Back("Pair not found.");
            p.Enabled = form.Enabled;
            p.Title = Trim(form.Title);
            p.Description = Trim(form.Description);
            p.FromCurrency = Trim(form.FromCurrency);
            p.ToCurrency = Trim(form.ToCurrency);
            p.FromPerUnit = form.FromPerUnit;
            p.Step = form.Step <= 0m ? 1m : form.Step;
            p.MinTo = form.MinTo;
            p.MaxToPerTx = Math.Max(0m, form.MaxToPerTx);
            p.DailyCapTo = Math.Max(0m, form.DailyCapTo);
            p.LifetimeCapTo = Math.Max(0m, form.LifetimeCapTo);
            p.MinLevel = Math.Max(0, form.MinLevel);
            p.SortOrder = form.SortOrder;
            p.FromUtc = form.FromUtc;
            p.ToUtc = form.ToUtc;
            return SaveConfig(cfg, $"Saved '{key}' — live on the next catalog read (~15 s).");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Toggle(string key)
        {
            var cfg = Effective();
            var p = cfg.Find(key);
            if (p == null) return Back("Pair not found.");
            p.Enabled = !p.Enabled;
            return SaveConfig(cfg, $"'{key}' is now {(p.Enabled ? "ON" : "off")}.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(string key)
        {
            var cfg = Effective();
            var p = cfg.Find(key);
            if (p == null) return Back("Pair not found.");
            cfg.Pairs.Remove(p);
            return SaveConfig(cfg, $"Deleted '{key}'. Past exchanges keep their own record of the rate they used.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveJson(string json)
        {
            var cfg = ExchangeCatalog.TryParse(json);
            if (cfg == null) return Back("Invalid JSON — nothing saved.");
            return SaveConfig(cfg, "Saved from JSON.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reset()
        {
            try
            {
                _backups.BackupAsync(ExchangeCatalog.RedisKey).GetAwaiter().GetResult();
                _redis.GetDatabase().KeyDelete(ExchangeCatalog.RedisKey);
                TempData["Saved"] = "Reset to the built-in catalog (E-03: chips → Kash, 1,000,000 : 1). The previous catalog was backed up first.";
            }
            catch { TempData["Error"] = "Could not reach Redis."; }
            return RedirectToAction(nameof(Index));
        }

        /// <summary>The runtime kill switch (<c>Exchange:Enabled</c> on the settings hash) — off = every quote/exchange answers "closed".</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Switch(bool on)
        {
            try
            {
                _redis.GetDatabase().HashSet(SettingsHashKey, ExchangeCatalog.EnabledSwitch, on ? "true" : "false");
                TempData["Saved"] = on ? "Exchange switched ON." : "Exchange switched OFF — every quote and exchange now answers \"closed\" (live within ~15 s).";
            }
            catch { TempData["Error"] = "Could not reach Redis."; }
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult Download(string file)
        {
            var json = _backups.Read(ExchangeCatalog.RedisKey, file);
            if (json == null) return NotFound();
            return File(Encoding.UTF8.GetBytes(json), "application/json", $"khela-exchange-{file}");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Restore(string file)
        {
            var json = _backups.Read(ExchangeCatalog.RedisKey, file);
            if (json == null) return Back("Backup not found.");
            var cfg = ExchangeCatalog.TryParse(json);
            if (cfg == null) return Back("That backup does not parse.");
            return SaveConfig(cfg, $"Restored {file}.");
        }

        // ======================================================================= ledger

        [HttpGet]
        public async Task<IActionResult> Ledger(string q, string pair, int page = 0)
        {
            var vm = new ExchangeLedgerVm { Query = q, Pair = pair, Page = Math.Max(0, page) };
            var rows = _db.CurrencyExchanges.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(pair)) rows = rows.Where(x => x.PairKey == pair);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var s = q.Trim();
                if (Guid.TryParse(s, out var g)) rows = rows.Where(x => x.UserId == g || x.Id == g || x.RequestId == g);
                else
                {
                    var userIds = await _db.UserProfiles.AsNoTracking().Where(p => p.DisplayName.Contains(s) || p.PublicId == s).Select(p => p.UserId).Take(200).ToListAsync();
                    var byEmail = await _db.Users.AsNoTracking().Where(u => u.Email.Contains(s)).Select(u => u.Id).Take(200).ToListAsync();
                    foreach (var id in byEmail) if (Guid.TryParse(id, out var ug)) userIds.Add(ug);
                    if (userIds.Count == 0) { vm.NotFound = true; rows = rows.Where(x => false); }
                    else rows = rows.Where(x => userIds.Contains(x.UserId));
                }
            }
            vm.Total = await rows.CountAsync();
            var since = DateTime.UtcNow.AddDays(-30);
            vm.Totals30d = await _db.CurrencyExchanges.AsNoTracking()
                .Where(x => x.Status == "Completed" && x.CreatedAt >= since)
                .GroupBy(x => x.PairKey)
                .Select(g => new ExchangePairTotal { PairKey = g.Key, Count = g.Count(), FromAmount = g.Sum(x => x.FromAmount), ToAmount = g.Sum(x => x.ToAmount) })
                .OrderByDescending(t => t.Count).ToListAsync();
            var list = await rows.OrderByDescending(x => x.CreatedAt).Skip(vm.Page * PageSize).Take(PageSize).ToListAsync();
            var ids = list.Select(x => x.UserId).Distinct().ToList();
            var names = await _db.UserProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).Select(p => new { p.UserId, p.DisplayName }).ToListAsync();
            var byUser = names.ToDictionary(n => n.UserId, n => n.DisplayName);
            vm.Rows = list.Select(x => new ExchangeLedgerRow { Row = x, PlayerName = byUser.TryGetValue(x.UserId, out var n) ? n : null }).ToList();
            vm.Pairs = Effective().Pairs.Select(p => p.Key).OrderBy(k => k).ToList();
            return View(vm);
        }

        // ======================================================================= helpers

        private ExchangeCatalogConfig Effective()
        {
            try
            {
                var json = _redis.GetDatabase().StringGet(ExchangeCatalog.RedisKey);
                if (json.HasValue)
                {
                    var cfg = ExchangeCatalog.TryParse(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { /* Redis down → defaults, same as the game */ }
            return ExchangeCatalog.Defaults();
        }

        private bool Overridden()
        {
            try { return _redis.GetDatabase().KeyExists(ExchangeCatalog.RedisKey); }
            catch { return false; }
        }

        private string Eff(string key)
        {
            try { var v = _redis.GetDatabase().HashGet(SettingsHashKey, key); if (v.HasValue) return v!; }
            catch { }
            return _config[key] ?? "";
        }

        private static bool Flag(string v, bool defaultValue)
            => string.IsNullOrWhiteSpace(v) ? defaultValue : v is "true" or "True" or "1" or "on";

        private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private IActionResult Back(string error, string openEdit = null)
        {
            TempData["Error"] = error;
            return openEdit != null ? RedirectToAction(nameof(Index), new { edit = openEdit }) : RedirectToAction(nameof(Index));
        }

        private IActionResult SaveConfig(ExchangeCatalogConfig cfg, string message, string openEdit = null)
        {
            var problem = ExchangeCatalog.Validate(cfg);
            if (problem != null) return Back(problem + " Nothing saved.", openEdit);
            try
            {
                _backups.BackupAsync(ExchangeCatalog.RedisKey).GetAwaiter().GetResult();   // snapshot the OLD value first
                _redis.GetDatabase().StringSet(ExchangeCatalog.RedisKey, ExchangeCatalog.ToJson(cfg));
                TempData["Saved"] = message;
            }
            catch { TempData["Error"] = "Could not reach Redis to save."; }
            return openEdit != null ? RedirectToAction(nameof(Index), new { edit = openEdit }) : RedirectToAction(nameof(Index));
        }

        private ExchangeIndexVm BuildIndex()
        {
            var cfg = Effective();
            var vm = new ExchangeIndexVm
            {
                Config = cfg,
                Overridden = Overridden(),
                Json = ExchangeCatalog.ToJson(cfg),
                Backups = _backups.List(ExchangeCatalog.RedisKey).Take(20).ToList(),
                SwitchOn = Flag(Eff(ExchangeCatalog.EnabledSwitch), true),
                EditKey = Request.Query["edit"].ToString(),
            };
            vm.Notes.AddRange(ExchangeCatalog.RoundTripNotes(cfg));

            // Economy notes against the STORE ladder: what each pair means in dollars. Informational — the validator only
            // refuses what is provably a money printer (round trips); whether a rate is WISE is the admin's call, shown plainly.
            try
            {
                StoreCatalogConfig store = null;
                var json = _redis.GetDatabase().StringGet(StoreCatalog.RedisKey);
                if (json.HasValue) store = StoreCatalog.TryParse(json);
                store ??= StoreCatalog.Defaults();
                decimal bestChipsPerUsd = StoreCatalog.BestChipsPerUsd(store);
                decimal bestKashPerUsd = store.Products.Where(p => p != null && p.Enabled && p.UsdReference > 0m)
                    .Select(p => (p.Lines ?? new List<Khela.Common.Rewards.RewardGrant>()).Where(l => l != null && l.Kind == Khela.Common.Rewards.RewardKind.Currency && string.Equals(l.Id, "Kash", StringComparison.OrdinalIgnoreCase)).Sum(l => l.Amount) / p.UsdReference)
                    .DefaultIfEmpty(0m).Max();
                vm.BestChipsPerUsd = bestChipsPerUsd; vm.BestKashPerUsd = bestKashPerUsd;
                foreach (var p in cfg.Pairs.Where(p => p != null && p.Enabled))
                {
                    bool chipsToKash = string.Equals(p.FromCurrency, "Chips", StringComparison.OrdinalIgnoreCase) && string.Equals(p.ToCurrency, "Kash", StringComparison.OrdinalIgnoreCase);
                    bool kashToChips = string.Equals(p.FromCurrency, "Kash", StringComparison.OrdinalIgnoreCase) && string.Equals(p.ToCurrency, "Chips", StringComparison.OrdinalIgnoreCase);
                    if (chipsToKash && bestChipsPerUsd > 0m && bestKashPerUsd > 0m)
                    {
                        var kashPerUsdViaChips = bestChipsPerUsd / p.FromPerUnit;   // buy chips, convert
                        vm.Notes.Add($"{p.Key}: bought chips convert at {kashPerUsdViaChips:0.##} Kash/$ vs {bestKashPerUsd:0.##} Kash/$ direct — " +
                                     (kashPerUsdViaChips < bestKashPerUsd ? $"{bestKashPerUsd / kashPerUsdViaChips:0.#}× worse, never an arbitrage. OK." : "CHEAPER than buying Kash — this is a bridge; raise the rate."));
                    }
                    if (kashToChips && bestChipsPerUsd > 0m && bestKashPerUsd > 0m)
                    {
                        var chipsPerUsdViaKash = bestKashPerUsd / p.FromPerUnit;    // buy Kash, convert
                        vm.Notes.Add($"{p.Key}: bought Kash converts at {chipsPerUsdViaKash:#,0} chips/$ vs {bestChipsPerUsd:#,0} chips/$ from the best pack — " +
                                     (chipsPerUsdViaKash < bestChipsPerUsd ? "worse than the pack. OK." : "BEATS the chip packs — Kash becomes the cheap way to buy chips (a bridge). Raise the rate."));
                    }
                }
            }
            catch { }

            try
            {
                var since = DateTime.UtcNow.AddDays(-30);
                vm.Count30d = _db.CurrencyExchanges.Count(x => x.Status == "Completed" && x.CreatedAt >= since);
                vm.CountTotal = _db.CurrencyExchanges.Count(x => x.Status == "Completed");
            }
            catch { }
            return vm;
        }
    }

    public sealed class ExchangePairForm
    {
        public string Key { get; set; }
        public bool Enabled { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string FromCurrency { get; set; }
        public string ToCurrency { get; set; }
        public decimal FromPerUnit { get; set; }
        public decimal Step { get; set; }
        public decimal MinTo { get; set; }
        public decimal MaxToPerTx { get; set; }
        public decimal DailyCapTo { get; set; }
        public decimal LifetimeCapTo { get; set; }
        public int MinLevel { get; set; }
        public int SortOrder { get; set; }
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
    }

    public sealed class ExchangeIndexVm
    {
        public ExchangeCatalogConfig Config { get; set; }
        public bool Overridden { get; set; }
        public string Json { get; set; }
        public List<ConfigBackupInfo> Backups { get; set; } = new();
        public bool SwitchOn { get; set; }
        public List<string> Notes { get; set; } = new();
        public decimal BestChipsPerUsd { get; set; }
        public decimal BestKashPerUsd { get; set; }
        public int Count30d { get; set; }
        public int CountTotal { get; set; }
        public string EditKey { get; set; }
        public string Saved { get; set; }
        public string Error { get; set; }
    }

    public sealed class ExchangeLedgerVm
    {
        public string Query { get; set; }
        public string Pair { get; set; }
        public bool NotFound { get; set; }
        public int Page { get; set; }
        public int PageSize => 50;
        public int Total { get; set; }
        public List<ExchangeLedgerRow> Rows { get; set; } = new();
        public List<ExchangePairTotal> Totals30d { get; set; } = new();
        public List<string> Pairs { get; set; } = new();
    }

    public sealed class ExchangeLedgerRow
    {
        public CurrencyExchange Row { get; set; }
        public string PlayerName { get; set; }
    }

    public sealed class ExchangePairTotal
    {
        public string PairKey { get; set; }
        public int Count { get; set; }
        public decimal FromAmount { get; set; }
        public decimal ToAmount { get; set; }
    }
}
