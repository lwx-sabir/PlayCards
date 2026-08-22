using System.Globalization;
using System.Text;
using System.Text.Json;
using Khela.Common.Rewards;
using Khela.Common.Store;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Config;
using Khela.Game.Services.Pass;
using Khela.Game.Services.Piggy;
using Khela.Game.Services.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// The in-app store (docs/IAP_SPEC.md §7.2): the CATALOG editor over the Redis document <c>khela:store</c> (the same
    /// overlay the game reads; <see cref="StoreCatalog.Defaults"/> is the fallback; every save runs the game's own
    /// fail-closed <see cref="StoreCatalog.Validate"/> and takes a config backup), the PURCHASES ledger (read-only view of
    /// <c>StorePurchases</c> + events), and the PLATFORMS panel (the status the game process publishes). The two actions
    /// that touch money — re-drive and refund — are QUEUED in Redis and executed by the game's reconciler: the web
    /// process never pays or reverses anything itself.
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class StoreController : Controller
    {
        private const string SettingsHashKey = "khela:settings";
        private const int PageSize = 50;

        private readonly IConnectionMultiplexer _redis;
        private readonly IConfigBackupService _backups;
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;

        public StoreController(IConnectionMultiplexer redis, IConfigBackupService backups, AppDbContext db, IConfiguration config)
        {
            _redis = redis; _backups = backups; _db = db; _config = config;
        }

        // ======================================================================= catalog

        [HttpGet]
        public IActionResult Index()
        {
            var vm = BuildIndex();
            vm.Saved = TempData["Saved"] as string;
            vm.Error = TempData["Error"] as string;
            return View(vm);
        }

        /// <summary>Add a product (disabled, with a sensible template) and open it in the editor.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(string id, string section)
        {
            var cfg = Effective();
            id = (id ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(id)) return Back("A product needs an id (e.g. chips_08).");
            if (cfg.Find(id) != null) return Back($"'{id}' already exists.");
            cfg.Products.Add(new StoreProductDef
            {
                Id = id, Enabled = false, Section = string.IsNullOrWhiteSpace(section) ? "chips" : section.Trim(),
                SortOrder = (cfg.Products.Count + 1) * 10, Title = id, StoreIds = StoreCatalog.BothStores(id), UsdReference = 0.99m,
                Lines = new List<RewardGrant> { RewardGrant.Currency("Chips", 1_000_000m) },
            });
            return SaveConfig(cfg, $"Created '{id}' (disabled — fill it in, then enable).", openEdit: id);
        }

        /// <summary>The edit modal's form — one product, every field.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Save(StoreProductForm form)
        {
            var cfg = Effective();
            var id = (form.Id ?? "").Trim();
            var p = cfg.Find(id);
            if (p == null) return Back("Product not found.");

            var lines = PassRewardText.Parse(form.Lines, out var lineError);
            if (lines == null) return Back($"'{id}' lines: {lineError}", openEdit: id);

            p.Enabled = form.Enabled;
            p.ProductType = form.ProductType;
            p.Section = Trim(form.Section);
            p.SortOrder = form.SortOrder;
            p.Title = Trim(form.Title);
            p.Description = Trim(form.Description);
            p.Badge = Trim(form.Badge);
            p.BonusPercent = Math.Max(0, form.BonusPercent);
            p.Featured = form.Featured;
            p.UsdReference = form.UsdReference;
            p.Images = (form.Images ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            p.StoreIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Sid(StorePlatform platform, string value) { if (!string.IsNullOrWhiteSpace(value)) p.StoreIds[platform.ToString()] = value.Trim(); }
            Sid(StorePlatform.GooglePlay, form.StoreIdGooglePlay);
            Sid(StorePlatform.AppStore, form.StoreIdAppStore);
            Sid(StorePlatform.Web, form.StoreIdWeb);
            Sid(StorePlatform.Amazon, form.StoreIdAmazon);
            p.Lines = lines;
            if (string.IsNullOrWhiteSpace(form.EffectType) || form.EffectType == "none")
                p.Effect = null;
            else
            {
                p.Effect = new StoreEffectDef { Type = form.EffectType.Trim(), Arg = Trim(form.EffectArg) };
                if (string.Equals(p.Effect.Type, StoreCatalog.EffectPiggyBreak, StringComparison.OrdinalIgnoreCase))
                    p.Effect.Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [StoreCatalog.PiggyTierParam] = Math.Max(1, form.EffectTier).ToString(CultureInfo.InvariantCulture) };
            }
            p.Availability = new StoreAvailabilityDef
            {
                FromUtc = form.FromUtc, ToUtc = form.ToUtc,
                MaxPerUser = Math.Max(0, form.MaxPerUser), MaxPerUserPerDay = Math.Max(0, form.MaxPerUserPerDay), MinLevel = Math.Max(0, form.MinLevel),
            };
            return SaveConfig(cfg, $"Saved '{id}' — live on the next catalog read (~15 s).");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Toggle(string id)
        {
            var cfg = Effective();
            var p = cfg.Find(id);
            if (p == null) return Back("Product not found.");
            p.Enabled = !p.Enabled;
            return SaveConfig(cfg, $"'{id}' is now {(p.Enabled ? "ON SALE" : "off")}.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(string id)
        {
            var cfg = Effective();
            var p = cfg.Find(id);
            if (p == null) return Back("Product not found.");
            // Purchases keep their own snapshot of what they paid, so history survives; a PENDING order for it would fail to
            // resolve though — prefer disabling anything that was ever on sale.
            cfg.Products.Remove(p);
            return SaveConfig(cfg, $"Deleted '{id}'. Past purchases keep their snapshot; disable rather than delete anything that was ever live.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveJson(string json)
        {
            var cfg = StoreCatalog.TryParse(json);
            if (cfg == null) return Back("Invalid JSON — nothing saved.");
            return SaveConfig(cfg, "Saved from JSON.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reset()
        {
            try
            {
                _backups.BackupAsync(StoreCatalog.RedisKey).GetAwaiter().GetResult();
                _redis.GetDatabase().KeyDelete(StoreCatalog.RedisKey);
                TempData["Saved"] = "Reset to the built-in catalog (the §3.1 ladder). The previous catalog was backed up first.";
            }
            catch { TempData["Error"] = "Could not reach Redis."; }
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult Download(string file)
        {
            var json = _backups.Read(StoreCatalog.RedisKey, file);
            if (json == null) return NotFound();
            return File(Encoding.UTF8.GetBytes(json), "application/json", $"khela-store-{file}");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Restore(string file)
        {
            var json = _backups.Read(StoreCatalog.RedisKey, file);
            if (json == null) return Back("That backup is gone.");
            if (StoreCatalog.TryParse(json) == null) return Back("That backup won't parse — download it and check by hand.");
            var vm = BuildIndex(json);
            vm.Error = $"Loaded backup {file} into the editor below. Review it, then press Save JSON to make it live.";
            return View(nameof(Index), vm);
        }

        // ======================================================================= purchases

        [HttpGet]
        public async Task<IActionResult> Purchases(string q, StorePurchaseStatus? status, StorePlatform? platform, string test, int page = 0)
        {
            var vm = new StorePurchasesVm { Query = q, Status = status, Platform = platform, Test = test, Page = Math.Max(0, page) };
            var query = _db.StorePurchases.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
            {
                q = q.Trim();
                Guid? userId = null;
                if (Guid.TryParse(q, out var g))
                {
                    // a purchase id or a user id
                    if (await _db.StorePurchases.AnyAsync(s => s.Id == g)) return RedirectToAction(nameof(Purchase), new { id = g });
                    userId = g;
                }
                else if (q.Contains('@'))
                {
                    var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == q);
                    if (u != null && Guid.TryParse(u.Id, out var ug)) userId = ug;
                }
                else
                {
                    var prof = await _db.UserProfiles.AsNoTracking().Where(p => p.DisplayName.Contains(q)).OrderBy(p => p.DisplayName).FirstOrDefaultAsync();
                    if (prof != null) userId = prof.UserId;
                    else query = query.Where(s => s.StoreOrderId == q || s.StoreTransactionId == q || s.ProductId == q);   // an order id / product id
                }
                if (userId.HasValue) query = query.Where(s => s.UserId == userId.Value);
                else if (!q.Contains('@') && !Guid.TryParse(q, out _)) { /* product/order filter applied above */ }
                else { vm.NotFound = true; return View(vm); }
            }
            if (status.HasValue) query = query.Where(s => s.Status == status.Value);
            if (platform.HasValue) query = query.Where(s => s.Platform == platform.Value);
            if (string.Equals(test, "real", StringComparison.OrdinalIgnoreCase)) query = query.Where(s => !s.IsTest);
            else if (string.Equals(test, "test", StringComparison.OrdinalIgnoreCase)) query = query.Where(s => s.IsTest);

            vm.Total = await query.CountAsync();
            vm.RevenueUsd = await query.Where(s => !s.IsTest && (s.Status == StorePurchaseStatus.Granted || s.Status == StorePurchaseStatus.Refunded)).SumAsync(s => (decimal?)s.UsdReference) ?? 0m;
            vm.RefundedUsd = await query.Where(s => !s.IsTest && s.Status == StorePurchaseStatus.Refunded).SumAsync(s => (decimal?)s.UsdReference) ?? 0m;
            var rows = await query.OrderByDescending(s => s.CreatedAt).Skip(vm.Page * PageSize).Take(PageSize)
                .Select(s => new StorePurchaseRow
                {
                    Id = s.Id, UserId = s.UserId, Platform = s.Platform, ProductId = s.ProductId, StoreProductId = s.StoreProductId,
                    StoreOrderId = s.StoreOrderId, ProductType = s.ProductType, Status = s.Status, IsTest = s.IsTest, Environment = s.Environment,
                    UsdReference = s.UsdReference, ClientPriceMicros = s.ClientPriceMicros, ClientPriceCurrency = s.ClientPriceCurrency,
                    Attempts = s.Attempts, LastError = s.LastError, CreatedAt = s.CreatedAt, GrantedAt = s.GrantedAt, CompletedAt = s.CompletedAt,
                    AcknowledgedAt = s.AcknowledgedAt, RefundedAt = s.RefundedAt,
                }).ToListAsync();
            var userIds = rows.Select(r => r.UserId).Distinct().ToList();
            var names = await _db.UserProfiles.AsNoTracking().Where(p => userIds.Contains(p.UserId)).Select(p => new { p.UserId, p.DisplayName }).ToListAsync();
            var byUser = names.ToDictionary(n => n.UserId, n => n.DisplayName);
            foreach (var r in rows) r.PlayerName = byUser.TryGetValue(r.UserId, out var n) ? n : null;
            vm.Rows = rows;
            vm.Saved = TempData["Saved"] as string;
            vm.Error = TempData["Error"] as string;
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> Purchase(Guid id)
        {
            var row = await _db.StorePurchases.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (row == null) return NotFound();
            var events = await _db.StoreEvents.AsNoTracking().Where(e => e.PurchaseId == id).OrderByDescending(e => e.ReceivedAt).Take(50).ToListAsync();
            var name = await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == row.UserId).Select(p => p.DisplayName).FirstOrDefaultAsync();
            var email = await _db.Users.AsNoTracking().Where(u => u.Id == row.UserId.ToString()).Select(u => u.Email).FirstOrDefaultAsync();
            return View(new StorePurchaseVm
            {
                Row = row, Events = events, PlayerName = name, Email = email,
                Fulfilment = Pretty(row.FulfilmentJson), Verifier = Pretty(row.VerifierJson), Snapshot = Pretty(row.CatalogSnapshotJson),
                Saved = TempData["Saved"] as string, Error = TempData["Error"] as string,
            });
        }

        /// <summary>Queue a re-drive; the game's reconciler executes it within one pass (~2 min).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Redrive(Guid id)
        {
            try
            {
                _redis.GetDatabase().ListRightPush(StoreReconciliationService.RedriveQueueKey, id.ToString("D"));
                TempData["Saved"] = "Re-drive queued — the game server picks it up on its next reconcile pass (≤ 2 min). Refresh to see the outcome.";
            }
            catch { TempData["Error"] = "Could not reach Redis."; }
            return RedirectToAction(nameof(Purchase), new { id });
        }

        /// <summary>Queue a manual refund/revoke; the game applies Store:Refunds:Policy (Rollback reverses the credit if unspent, else flags).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Refund(Guid id, string reason)
        {
            try
            {
                var safeReason = (reason ?? "manual").Replace('|', '/').Trim();
                _redis.GetDatabase().ListRightPush(StoreReconciliationService.RefundQueueKey, $"{id:D}|admin|{safeReason}");
                TempData["Saved"] = "Refund queued — the game server applies the refund policy on its next reconcile pass (≤ 2 min).";
            }
            catch { TempData["Error"] = "Could not reach Redis."; }
            return RedirectToAction(nameof(Purchase), new { id });
        }

        // ======================================================================= helpers

        private StoreCatalogConfig Effective()
        {
            try
            {
                var json = _redis.GetDatabase().StringGet(StoreCatalog.RedisKey);
                if (json.HasValue)
                {
                    var cfg = StoreCatalog.TryParse(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { /* Redis down → defaults, same as the game */ }
            return StoreCatalog.Defaults();
        }

        private bool Overridden()
        {
            try { return _redis.GetDatabase().KeyExists(StoreCatalog.RedisKey); }
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

        private bool AllowRandomPayloads() => Flag(Eff("Store:AllowRandomPayloads"), false);

        private IActionResult SaveConfig(StoreCatalogConfig cfg, string message, string openEdit = null)
        {
            var problem = StoreCatalog.Validate(cfg, allowRandomPayloads: AllowRandomPayloads());
            if (problem != null) return Back(problem + " Nothing saved.", openEdit);
            try
            {
                _backups.BackupAsync(StoreCatalog.RedisKey).GetAwaiter().GetResult();   // snapshot the OLD value first
                _redis.GetDatabase().StringSet(StoreCatalog.RedisKey, StoreCatalog.ToJson(cfg));
                TempData["Saved"] = message;
            }
            catch { TempData["Error"] = "Could not reach Redis to save."; }
            return openEdit != null ? RedirectToAction(nameof(Index), new { edit = openEdit }) : RedirectToAction(nameof(Index));
        }

        private StoreIndexVm BuildIndex(string json = null)
        {
            var cfg = Effective();
            var vm = new StoreIndexVm
            {
                Config = cfg,
                Overridden = Overridden(),
                Json = json ?? StoreCatalog.ToJson(cfg),
                Backups = _backups.List(StoreCatalog.RedisKey).Take(20).ToList(),
                StoreEnabled = Flag(Eff("Store:Enabled"), true),
                EditId = Request.Query["edit"].ToString(),
            };
            foreach (var p in new[] { StorePlatform.GooglePlay, StorePlatform.AppStore, StorePlatform.Fake, StorePlatform.Web })
                vm.PlatformEnabled[p] = Flag(Eff($"Store:{p}:Enabled"), p == StorePlatform.GooglePlay || p == StorePlatform.Fake);

            // Warnings: missing store ids on enabled platforms; piggy offers worse than the best pack (value guard).
            var enabledPlatforms = vm.PlatformEnabled.Where(kv => kv.Value && kv.Key != StorePlatform.Fake).Select(kv => kv.Key).ToList();
            vm.Warnings.AddRange(StoreCatalog.MissingStoreIds(cfg, enabledPlatforms));
            try
            {
                var tiers = PiggyConfig.ParseTiers(Eff("Piggy:Tiers"), new PiggyConfig().Tiers);
                var offers = new List<(string, decimal)>();
                for (int i = 0; i < tiers.Length; i++)
                {
                    int n = i + 1;
                    offers.Add(($"piggy_t{n}_full", tiers[i].MaxAmount));
                    offers.Add(($"piggy_t{n}_x2", tiers[i].MaxAmount * 2m));
                    offers.Add(($"piggy_t{n}_early", tiers[i].MaxAmount));
                }
                vm.Warnings.AddRange(StoreCatalog.PiggyValueWarnings(cfg, offers));
                vm.BestChipsPerUsd = StoreCatalog.BestChipsPerUsd(cfg);
            }
            catch { }

            // The game's published status (Platforms panel).
            try
            {
                var raw = _redis.GetDatabase().StringGet(StoreReconciliationService.StatusKey);
                if (raw.HasValue) vm.Status = JsonDocument.Parse((string)raw);
            }
            catch { }

            try
            {
                vm.PurchaseCount = _db.StorePurchases.Count();
                vm.PendingCount = _db.StorePurchases.Count(s => s.Status == StorePurchaseStatus.Pending || s.Status == StorePurchaseStatus.Verified);
                var since = DateTime.UtcNow.AddDays(-30);
                vm.RevenueUsd30d = _db.StorePurchases.Where(s => !s.IsTest && s.CreatedAt >= since && (s.Status == StorePurchaseStatus.Granted || s.Status == StorePurchaseStatus.Refunded)).Sum(s => (decimal?)s.UsdReference) ?? 0m;
            }
            catch { }
            return vm;
        }

        private static string Pretty(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { using var doc = JsonDocument.Parse(json); return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }); }
            catch { return json; }
        }

        private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private IActionResult Back(string error, string openEdit = null)
        {
            TempData["Error"] = error;
            return openEdit != null ? RedirectToAction(nameof(Index), new { edit = openEdit }) : RedirectToAction(nameof(Index));
        }
    }

    public sealed class StoreProductForm
    {
        public string Id { get; set; }
        public bool Enabled { get; set; }
        public StoreProductType ProductType { get; set; }
        public string Section { get; set; }
        public int SortOrder { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Badge { get; set; }
        public int BonusPercent { get; set; }
        public bool Featured { get; set; }
        public decimal UsdReference { get; set; }
        public string Images { get; set; }
        public string StoreIdGooglePlay { get; set; }
        public string StoreIdAppStore { get; set; }
        public string StoreIdWeb { get; set; }
        public string StoreIdAmazon { get; set; }
        /// <summary>Reward lines as text: "Chips 5000000, Kash 50" (the pass ladder's grammar).</summary>
        public string Lines { get; set; }
        public string EffectType { get; set; }
        public string EffectArg { get; set; }
        public int EffectTier { get; set; }
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
        public int MaxPerUser { get; set; }
        public int MaxPerUserPerDay { get; set; }
        public int MinLevel { get; set; }
    }

    public sealed class StoreIndexVm
    {
        public StoreCatalogConfig Config { get; set; }
        public bool Overridden { get; set; }
        public string Json { get; set; }
        public List<ConfigBackupInfo> Backups { get; set; } = new();
        public bool StoreEnabled { get; set; }
        public Dictionary<StorePlatform, bool> PlatformEnabled { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public decimal BestChipsPerUsd { get; set; }
        public JsonDocument Status { get; set; }
        public int PurchaseCount { get; set; }
        public int PendingCount { get; set; }
        public decimal RevenueUsd30d { get; set; }
        /// <summary>Product id to open in the edit modal on load (after Create / a failed Save).</summary>
        public string EditId { get; set; }
        public string Saved { get; set; }
        public string Error { get; set; }
    }

    public sealed class StorePurchasesVm
    {
        public string Query { get; set; }
        public StorePurchaseStatus? Status { get; set; }
        public StorePlatform? Platform { get; set; }
        public string Test { get; set; }
        public bool NotFound { get; set; }
        public int Page { get; set; }
        public int PageSize => 50;
        public int Total { get; set; }
        public decimal RevenueUsd { get; set; }
        public decimal RefundedUsd { get; set; }
        public List<StorePurchaseRow> Rows { get; set; } = new();
        public bool HasPrev => Page > 0;
        public bool HasNext => (Page + 1) * PageSize < Total;
        public string Saved { get; set; }
        public string Error { get; set; }
    }

    public sealed class StorePurchaseRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string PlayerName { get; set; }
        public StorePlatform Platform { get; set; }
        public string ProductId { get; set; }
        public string StoreProductId { get; set; }
        public string StoreOrderId { get; set; }
        public StoreProductType ProductType { get; set; }
        public StorePurchaseStatus Status { get; set; }
        public bool IsTest { get; set; }
        public string Environment { get; set; }
        public decimal UsdReference { get; set; }
        public long? ClientPriceMicros { get; set; }
        public string ClientPriceCurrency { get; set; }
        public int Attempts { get; set; }
        public string LastError { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? GrantedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public DateTime? RefundedAt { get; set; }
    }

    public sealed class StorePurchaseVm
    {
        public StorePurchase Row { get; set; }
        public List<StoreEvent> Events { get; set; } = new();
        public string PlayerName { get; set; }
        public string Email { get; set; }
        public string Fulfilment { get; set; }
        public string Verifier { get; set; }
        public string Snapshot { get; set; }
        public string Saved { get; set; }
        public string Error { get; set; }
    }
}
