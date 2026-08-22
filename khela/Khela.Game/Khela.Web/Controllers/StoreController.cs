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

        /// <summary>
        /// Create the products a piggy rung is missing, one per option. They arrive DISABLED at $0: a price is a
        /// decision, and a $0 product on sale would give the bank away — this removes the tedium, never the pricing.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CreatePiggyProducts()
        {
            var cfg = Effective();
            var tiers = PiggyConfig.ParseTiers(Eff("Piggy:Tiers"), new PiggyConfig().Tiers);
            var made = new List<string>();
            for (int i = 0; i < tiers.Length; i++)
            {
                int tier = i + 1;
                for (int k = 0; k < PiggyOptions.Length; k++)
                {
                    var (option, suffix, _) = PiggyOptions[k];
                    if (FindPiggyProduct(cfg, tier, option) != null) continue;
                    var id = $"piggy_t{tier}_{suffix}";
                    if (cfg.Find(id) != null) continue;   // the id is taken by something that is NOT this offer — never rewrite it
                    cfg.Products.Add(new StoreProductDef
                    {
                        Id = id,
                        Enabled = false,
                        Section = "piggy",
                        SortOrder = tier * 10 + k,
                        Title = option == "FullDouble" ? $"Piggy Bank T{tier} ×2" : option == "Early" ? $"Piggy Bank T{tier} (early)" : $"Piggy Bank T{tier}",
                        StoreIds = StoreCatalog.BothStores(id),
                        UsdReference = 0m,
                        Effect = new StoreEffectDef
                        {
                            Type = StoreCatalog.EffectPiggyBreak,
                            Arg = option,
                            Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [StoreCatalog.PiggyTierParam] = tier.ToString(CultureInfo.InvariantCulture) },
                        },
                    });
                    made.Add(id);
                }
            }
            if (made.Count == 0) return Back("Every piggy rung already has its three products.");
            return SaveConfig(cfg, $"Created {made.Count}: {string.Join(", ", made)} — all DISABLED at $0. Price them, then enable.");
        }

        /// <summary>
        /// The shop's tabs. A row with no key is dropped, which is also how you delete one. Removing a section that
        /// products still point at is refused rather than saved: the game's own validator rejects an unknown section,
        /// so the catalog would fail to load rather than merely look wrong.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveSections(string[] key, string[] title, string[] sort)
        {
            var cfg = Effective();
            var list = new List<StoreSectionDef>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; key != null && i < key.Length; i++)
            {
                var k = (key[i] ?? "").Trim();
                if (k.Length == 0) continue;
                if (!seen.Add(k)) return Back($"Section '{k}' is listed twice.");
                var t = title != null && i < title.Length ? Trim(title[i]) : null;
                int order = (i + 1) * 10;
                if (sort != null && i < sort.Length && int.TryParse(sort[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) order = s;
                list.Add(new StoreSectionDef { Key = k, Title = t ?? k, SortOrder = order });
            }

            if (list.Count > 0)
            {
                var orphans = cfg.Products
                    .Where(p => !string.IsNullOrWhiteSpace(p.Section) && !seen.Contains(p.Section.Trim()))
                    .Select(p => p.Id).ToList();
                if (orphans.Count > 0)
                    return Back($"That would orphan {orphans.Count} product(s): {string.Join(", ", orphans.Take(6))}{(orphans.Count > 6 ? " …" : "")}. Move them to a surviving section first.");
            }

            cfg.Sections = list;
            return SaveConfig(cfg, list.Count == 0
                ? "Sections cleared — products keep their section text and the shop groups them in catalog order."
                : $"Saved {list.Count} section(s).");
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

        /// <summary>Full / ×2 / Early — the three ways one rung is sold, and what each pays out of the bank.</summary>
        private static readonly (string Option, string Suffix, decimal Multiplier)[] PiggyOptions =
        {
            ("Full", "full", 1m), ("FullDouble", "x2", 2m), ("Early", "early", 1m),
        };

        /// <summary>The product that sells a rung — matched on the EFFECT, exactly as the client resolves it.</summary>
        private static StoreProductDef FindPiggyProduct(StoreCatalogConfig cfg, int tier, string option)
            => cfg?.Products?.FirstOrDefault(p => p?.Effect != null
                   && string.Equals(p.Effect.Type, StoreCatalog.EffectPiggyBreak, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(p.Effect.Arg, option, StringComparison.OrdinalIgnoreCase)
                   && StoreCatalog.PiggyTierOf(p) == tier);

        /// <summary>
        /// The piggy ladder joined to the products that sell it. The two halves live in different documents — capacity
        /// comes from <c>Piggy:Tiers</c> (Settings ▸ Piggy), price from this catalog — and NOTHING keeps them in step,
        /// because the payout is always whatever the bank holds. So re-sizing a rung silently re-prices it, and a rung
        /// added past the last product becomes a bank nobody can buy. This table is what makes both visible.
        /// </summary>
        private List<PiggyLadderRow> BuildPiggyLadder(StoreCatalogConfig cfg)
        {
            var tiers = PiggyConfig.ParseTiers(Eff("Piggy:Tiers"), new PiggyConfig().Tiers);
            var rows = new List<PiggyLadderRow>();
            for (int i = 0; i < tiers.Length; i++)
            {
                int tier = i + 1;
                // The top rung has no ceiling: it belongs to every player at that level AND ABOVE.
                string levels;
                if (i + 1 < tiers.Length)
                {
                    int last = tiers[i + 1].MinLevel - 1;
                    levels = last > tiers[i].MinLevel ? $"{tiers[i].MinLevel}–{last}" : tiers[i].MinLevel.ToString(CultureInfo.InvariantCulture);
                }
                else levels = $"{tiers[i].MinLevel}+";

                var row = new PiggyLadderRow { Tier = tier, MinLevel = tiers[i].MinLevel, Levels = levels, Capacity = tiers[i].MaxAmount };
                foreach (var (option, suffix, multiplier) in PiggyOptions)
                {
                    var p = FindPiggyProduct(cfg, tier, option);
                    row.Offers.Add(new PiggyOfferRow
                    {
                        Option = option,
                        ProductId = p?.Id ?? $"piggy_t{tier}_{suffix}",
                        Exists = p != null,
                        Enabled = p != null && p.Enabled,
                        Usd = p?.UsdReference ?? 0m,
                        Chips = tiers[i].MaxAmount * multiplier,
                    });
                }
                rows.Add(row);
            }
            return rows;
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
                vm.PiggyLadder = BuildPiggyLadder(cfg);
                var offers = vm.PiggyLadder.SelectMany(r => r.Offers).ToList();
                vm.PiggyMissing = offers.Count(o => !o.Exists);
                vm.Warnings.AddRange(StoreCatalog.PiggyValueWarnings(cfg, offers.Select(o => (o.ProductId, o.Chips))));
                // A rung with no product is a bank its owners can never buy — the value guard SKIPS those silently
                // (it can only price a product it can find), so say it out loud here or it stays invisible.
                foreach (var row in vm.PiggyLadder.Where(r => r.Offers.Any(o => !o.Exists)))
                    vm.Warnings.Add($"piggy tier {row.Tier} (level {row.Levels}, {row.Capacity:N0} chips) has no product for " +
                                    string.Join(" / ", row.Offers.Where(o => !o.Exists).Select(o => o.ProductId)) +
                                    " — those players cannot break their bank.");
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

    /// <summary>One rung of the piggy ladder: what it holds, who gets it, and the three products that sell it.</summary>
    public sealed class PiggyLadderRow
    {
        public int Tier { get; set; }
        public int MinLevel { get; set; }
        /// <summary>Level band, e.g. "10–14"; the top rung reads "25+" — it belongs to that level and above.</summary>
        public string Levels { get; set; }
        public decimal Capacity { get; set; }
        public List<PiggyOfferRow> Offers { get; set; } = new();
    }

    public sealed class PiggyOfferRow
    {
        public string Option { get; set; }
        public string ProductId { get; set; }
        public bool Exists { get; set; }
        public bool Enabled { get; set; }
        public decimal Usd { get; set; }
        /// <summary>What it pays: capacity (×2 for FullDouble). Early is an upper bound — a bank bought early is not full.</summary>
        public decimal Chips { get; set; }
        public decimal ChipsPerUsd => Usd > 0m ? Chips / Usd : 0m;
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
        /// <summary>The piggy ladder (Settings ▸ Piggy) joined to the catalog products that sell each rung.</summary>
        public List<PiggyLadderRow> PiggyLadder { get; set; } = new();
        /// <summary>Rung/option pairs with no product — each one is a bank its owners cannot buy.</summary>
        public int PiggyMissing { get; set; }
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
