using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Store;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Store
{
    /// <summary>
    /// The store's kill switches: <c>Store:Enabled</c> and <c>Store:{Platform}:Enabled</c>, read from the <c>khela:settings</c>
    /// Redis overlay FIRST (the admin dashboard writes it; instant, no restart) and from appsettings otherwise. Credentials
    /// and paths are deliberately NOT overridable this way — a switch can turn a platform off at any time, but turning
    /// one ON additionally needs its verifier to load (docs/IAP_SPEC.md §7.1).
    /// </summary>
    public static class StoreSwitches
    {
        public const string SettingsHashKey = "khela:settings";
        public const string EnabledField = "Store:Enabled";

        public static string PlatformField(StorePlatform platform) => $"Store:{platform}:Enabled";

        /// <summary>The whole store on/off. Default (no config at all) = true.</summary>
        public static Task<bool> StoreEnabledAsync(IRedisService redis, IConfiguration config)
            => ReadAsync(redis, config, EnabledField, defaultValue: true);

        /// <summary>One platform on/off. Default: GooglePlay + Fake on, everything else off until configured.</summary>
        public static Task<bool> PlatformEnabledAsync(IRedisService redis, IConfiguration config, StorePlatform platform)
        {
            bool def = platform == StorePlatform.GooglePlay || platform == StorePlatform.Fake;
            return ReadAsync(redis, config, PlatformField(platform), def);
        }

        /// <summary>Any on/off store knob: overlay field ?? appsettings ?? <paramref name="defaultValue"/>.</summary>
        public static Task<bool> BoolAsync(IRedisService redis, IConfiguration config, string field, bool defaultValue)
            => ReadAsync(redis, config, field, defaultValue);

        /// <summary>Any text store knob (e.g. <c>Store:Refunds:Policy</c>): overlay field ?? appsettings ?? default.</summary>
        public static async Task<string> StringAsync(IRedisService redis, IConfiguration config, string field, string defaultValue)
        {
            var value = config?[field];
            if (string.IsNullOrWhiteSpace(value)) value = defaultValue;
            try
            {
                if (redis != null)
                {
                    var v = await redis.GetDatabase().HashGetAsync(SettingsHashKey, field);
                    if (v.HasValue && !string.IsNullOrWhiteSpace(v)) value = (string)v;
                }
            }
            catch { }
            return value;
        }

        /// <summary>Any numeric store knob (e.g. <c>Store:XpPerUsd</c>): overlay field ?? appsettings ?? default.</summary>
        public static async Task<decimal> DecimalAsync(IRedisService redis, IConfiguration config, string field, decimal defaultValue)
        {
            decimal value = config?.GetValue(field, defaultValue) ?? defaultValue;
            try
            {
                if (redis != null)
                {
                    var v = await redis.GetDatabase().HashGetAsync(SettingsHashKey, field);
                    if (v.HasValue && decimal.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var overlay)) value = overlay;
                }
            }
            catch { }
            return value;
        }

        private static async Task<bool> ReadAsync(IRedisService redis, IConfiguration config, string field, bool defaultValue)
        {
            bool value = config?.GetValue(field, defaultValue) ?? defaultValue;
            try
            {
                if (redis != null)
                {
                    var v = await redis.GetDatabase().HashGetAsync(SettingsHashKey, field);
                    if (v.HasValue)
                    {
                        var s = ((string)v).Trim();
                        if (bool.TryParse(s, out var overlay)) value = overlay;
                        else if (s == "1" || string.Equals(s, "on", StringComparison.OrdinalIgnoreCase)) value = true;
                        else if (s == "0" || string.Equals(s, "off", StringComparison.OrdinalIgnoreCase)) value = false;
                    }
                }
            }
            catch { /* Redis down → config wins; a switch must never take the store down by being unreadable */ }
            return value;
        }
    }

    /// <summary>
    /// The store catalog as the server and the client see it: the effective document (admin override or defaults),
    /// per-platform product resolution, and the per-user availability the intent check enforces.
    /// </summary>
    public interface IStoreCatalogService
    {
        /// <summary>The effective catalog: the Redis override if it parses AND validates, else code defaults. Cached ~15 s.</summary>
        Task<StoreCatalogConfig> GetConfigAsync();

        /// <summary>The catalog for one platform, with per-user availability. Products not sold on the platform are omitted.</summary>
        Task<StoreCatalogDto> GetCatalogAsync(StorePlatform platform, Guid userId);

        /// <summary>Is this user allowed to buy this product on this platform RIGHT NOW (limits, window, level, switches)? Cheap; no writes.</summary>
        Task<StoreAvailability> CheckAsync(StorePlatform platform, Guid userId, string productId, StoreCatalogConfig cfg = null);

        /// <summary>Admin save: validate fail-closed, write the Redis document, drop the cache. Returns the first error or null.</summary>
        Task<string> SaveAsync(StoreCatalogConfig cfg);

        /// <summary>Forget the cached document (after an admin save or a seed-file apply).</summary>
        void Invalidate();
    }

    /// <summary>Outcome of an availability check.</summary>
    public sealed class StoreAvailability
    {
        public bool Ok { get; set; }
        public string Reason { get; set; }
        public StoreProductDef Product { get; set; }
        public string StoreProductId { get; set; }
        public int PurchasedCount { get; set; }
        public bool StoreEnabled { get; set; }
        public bool PlatformEnabled { get; set; }

        public static StoreAvailability No(string reason, StoreProductDef product = null) => new StoreAvailability { Ok = false, Reason = reason, Product = product };
    }

    public sealed class StoreCatalogService : IStoreCatalogService
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
        private static readonly object Gate = new object();
        private static StoreCatalogConfig _cached;
        private static DateTime _cachedAtUtc;

        private readonly AppDbContext _db;
        private readonly IRedisService _redis;
        private readonly IConfiguration _config;
        private readonly ILogger<StoreCatalogService> _logger;

        public StoreCatalogService(AppDbContext db, IRedisService redis, IConfiguration config, ILogger<StoreCatalogService> logger)
        {
            _db = db; _redis = redis; _config = config; _logger = logger;
        }

        public async Task<StoreCatalogConfig> GetConfigAsync()
        {
            lock (Gate)
            {
                if (_cached != null && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return _cached;
            }

            StoreCatalogConfig cfg = null;
            try
            {
                var json = await _redis.GetDatabase().StringGetAsync(StoreCatalog.RedisKey);
                if (json.HasValue)
                {
                    cfg = StoreCatalog.TryParse(json);
                    if (cfg == null)
                        _logger.LogWarning("khela:store override is unparseable — falling back to the default catalog.");
                    else
                    {
                        var err = StoreCatalog.Validate(cfg, allowRandomPayloads: await AllowRandomPayloadsAsync());
                        if (err != null)
                        {
                            // Fail CLOSED to the defaults rather than half-apply: a catalog that sells the wrong thing is worse than none.
                            _logger.LogError("khela:store override is INVALID ({Error}) — falling back to the default catalog.", err);
                            cfg = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "khela:store could not be read — falling back to the default catalog.");
            }
            cfg ??= StoreCatalog.Defaults();

            lock (Gate) { _cached = cfg; _cachedAtUtc = DateTime.UtcNow; }
            return cfg;
        }

        public async Task<StoreCatalogDto> GetCatalogAsync(StorePlatform platform, Guid userId)
        {
            var cfg = await GetConfigAsync();
            bool storeOn = cfg.Enabled && await StoreSwitches.StoreEnabledAsync(_redis, _config);
            bool platformOn = await StoreSwitches.PlatformEnabledAsync(_redis, _config, platform);
            var now = DateTime.UtcNow;

            var sold = cfg.Products.Where(p => p != null && p.Enabled && StoreCatalog.StoreIdFor(p, platform) != null).ToList();

            // One query for every count this user has on these products (total + today) instead of one per card.
            var ids = sold.Select(p => p.Id).ToList();
            var dayStart = now.Date;
            var counts = ids.Count == 0
                ? new List<PurchaseCount>()
                : await _db.StorePurchases.AsNoTracking()
                    .Where(s => s.UserId == userId && ids.Contains(s.ProductId) && s.Status == StorePurchaseStatus.Granted)
                    .GroupBy(s => s.ProductId)
                    .Select(g => new PurchaseCount { ProductId = g.Key, Total = g.Count(), Today = g.Count(s => s.CreatedAt >= dayStart) })
                    .ToListAsync();
            var byId = counts.ToDictionary(c => c.ProductId, StringComparer.OrdinalIgnoreCase);

            int level = await LevelAsync(userId);

            var products = new List<StoreProductDto>();
            foreach (var p in sold.OrderBy(p => p.SortOrder).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                byId.TryGetValue(p.Id, out var count);
                var reason = Ineligible(p, count?.Total ?? 0, count?.Today ?? 0, level, now);
                if (!storeOn) reason ??= "The store is closed right now.";
                else if (!platformOn) reason ??= "Purchases are not available on this platform yet.";

                products.Add(new StoreProductDto
                {
                    Id = p.Id,
                    StoreProductId = StoreCatalog.StoreIdFor(p, platform),
                    ProductType = p.ProductType,
                    Section = p.Section,
                    SortOrder = p.SortOrder,
                    Title = p.Title,
                    Description = p.Description,
                    Badge = p.Badge,
                    BonusPercent = p.BonusPercent,
                    Featured = p.Featured,
                    Images = p.Images?.ToList() ?? new List<string>(),
                    Lines = p.Lines?.ToList() ?? new List<Khela.Common.Rewards.RewardGrant>(),
                    Effect = p.Effect == null ? null : new StoreEffectDto { Type = p.Effect.Type, Arg = p.Effect.Arg, Params = p.Effect.Params == null ? null : new Dictionary<string, string>(p.Effect.Params) },
                    UsdReference = p.UsdReference,
                    AvailableToUtc = p.Availability?.ToUtc,
                    Purchasable = reason == null,
                    Reason = reason,
                    PurchasedCount = count?.Total ?? 0,
                    MaxPerUser = p.Availability?.MaxPerUser ?? 0,
                    MaxPerUserPerDay = p.Availability?.MaxPerUserPerDay ?? 0,
                    MinLevel = p.Availability?.MinLevel ?? 0,
                });
            }

            return new StoreCatalogDto
            {
                Platform = platform,
                Enabled = storeOn,
                PlatformEnabled = platformOn,
                Version = cfg.Version,
                Sections = (cfg.Sections ?? new List<StoreSectionDef>()).OrderBy(s => s.SortOrder)
                    .Select(s => new StoreSectionDto { Key = s.Key, Title = s.Title, SortOrder = s.SortOrder }).ToList(),
                Products = products,
                ServerTimeUtc = now,
            };
        }

        public async Task<StoreAvailability> CheckAsync(StorePlatform platform, Guid userId, string productId, StoreCatalogConfig cfg = null)
        {
            cfg ??= await GetConfigAsync();
            var result = new StoreAvailability
            {
                StoreEnabled = cfg.Enabled && await StoreSwitches.StoreEnabledAsync(_redis, _config),
                PlatformEnabled = await StoreSwitches.PlatformEnabledAsync(_redis, _config, platform),
            };

            var p = cfg.Find(productId);
            if (p == null || !p.Enabled) { result.Reason = "This product is not available."; return result; }
            result.Product = p;
            result.StoreProductId = StoreCatalog.StoreIdFor(p, platform);
            if (result.StoreProductId == null) { result.Reason = "This product is not sold on this platform."; return result; }
            if (!result.StoreEnabled) { result.Reason = "The store is closed right now."; return result; }
            if (!result.PlatformEnabled) { result.Reason = "Purchases are not available on this platform yet."; return result; }

            var now = DateTime.UtcNow;
            var dayStart = now.Date;
            var rows = await _db.StorePurchases.AsNoTracking()
                .Where(s => s.UserId == userId && s.ProductId == p.Id && s.Status == StorePurchaseStatus.Granted)
                .Select(s => s.CreatedAt).ToListAsync();
            result.PurchasedCount = rows.Count;
            int today = rows.Count(t => t >= dayStart);

            result.Reason = Ineligible(p, rows.Count, today, await LevelAsync(userId), now);
            result.Ok = result.Reason == null;
            return result;
        }

        public async Task<string> SaveAsync(StoreCatalogConfig cfg)
        {
            var err = StoreCatalog.Validate(cfg, allowRandomPayloads: await AllowRandomPayloadsAsync());
            if (err != null) return err;
            await _redis.GetDatabase().StringSetAsync(StoreCatalog.RedisKey, StoreCatalog.ToJson(cfg));
            Invalidate();
            _logger.LogInformation("Store catalog saved: {Count} products, version {Version}.", cfg.Products.Count, cfg.Version);
            return null;
        }

        public void Invalidate()
        {
            lock (Gate) { _cached = null; _cachedAtUtc = default; }
        }

        // ---- helpers ----

        private Task<bool> AllowRandomPayloadsAsync() => StoreSwitches.BoolAsync(_redis, _config, "Store:AllowRandomPayloads", false);

        /// <summary>Why a product can't be bought by this player right now — null when it can. Pure on its inputs.</summary>
        public static string Ineligible(StoreProductDef p, int totalCount, int todayCount, int level, DateTime nowUtc)
        {
            var a = p.Availability ?? new StoreAvailabilityDef();
            if (a.FromUtc.HasValue && nowUtc < a.FromUtc.Value) return "Not available yet.";
            if (a.ToUtc.HasValue && nowUtc >= a.ToUtc.Value) return "This offer has ended.";
            if (a.MinLevel > 0 && level < a.MinLevel) return $"Unlocks at level {a.MinLevel}.";
            if (a.MaxPerUser > 0 && totalCount >= a.MaxPerUser) return a.MaxPerUser == 1 ? "Already purchased." : "Purchase limit reached.";
            if (a.MaxPerUserPerDay > 0 && todayCount >= a.MaxPerUserPerDay) return "Daily limit reached — come back tomorrow.";
            return null;
        }

        private async Task<int> LevelAsync(Guid userId)
        {
            var level = await _db.UserProfiles.AsNoTracking().Where(u => u.UserId == userId).Select(u => (int?)u.Level).FirstOrDefaultAsync();
            return level ?? 1;
        }

        private sealed class PurchaseCount
        {
            public string ProductId { get; set; }
            public int Total { get; set; }
            public int Today { get; set; }
        }
    }
}
