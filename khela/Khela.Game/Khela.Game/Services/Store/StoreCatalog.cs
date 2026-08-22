using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Khela.Common.Rewards;
using Khela.Common.Store;
using Khela.Game.Services.Rewards;

namespace Khela.Game.Services.Store
{
    /// <summary>A shop tab. Products reference sections by <see cref="Key"/>.</summary>
    public sealed class StoreSectionDef
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public int SortOrder { get; set; }
    }

    /// <summary>A kind-specific fulfilment (piggy break, golden pass, VIP booster). One grant handler per <see cref="Type"/>.</summary>
    public sealed class StoreEffectDef
    {
        public string Type { get; set; }
        public string Arg { get; set; }
        public Dictionary<string, string> Params { get; set; }

        public string Param(string key)
            => Params != null && key != null && Params.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>Per-user eligibility. Enforced at purchase INTENT (before the store sheet); at redeem a breach is flagged, never refused — money taken ⇒ fulfil.</summary>
    public sealed class StoreAvailabilityDef
    {
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
        /// <summary>0 = unlimited.</summary>
        public int MaxPerUser { get; set; }
        /// <summary>0 = unlimited.</summary>
        public int MaxPerUserPerDay { get; set; }
        public int MinLevel { get; set; }
    }

    /// <summary>
    /// One product in the server-owned catalog. <see cref="Id"/> is the stable id Reza types on a button; the per-platform
    /// store-product ids live in <see cref="StoreIds"/> (a platform with no entry does not sell it; the Fake store always
    /// sells everything under <see cref="Id"/>). What it pays = <see cref="Lines"/> (currency / XP / … as <see cref="RewardGrant"/>
    /// lines) plus an optional <see cref="Effect"/>.
    /// </summary>
    public sealed class StoreProductDef
    {
        public string Id { get; set; }
        public bool Enabled { get; set; } = true;
        public StoreProductType ProductType { get; set; } = StoreProductType.Consumable;
        public string Section { get; set; }
        public int SortOrder { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Badge { get; set; }
        public int BonusPercent { get; set; }
        public bool Featured { get; set; }
        public List<string> Images { get; set; } = new List<string>();
        /// <summary>Store-product id per platform, keyed by <see cref="StorePlatform"/> NAME ("GooglePlay", "AppStore", …). Missing/empty = not sold there.</summary>
        public Dictionary<string, string> StoreIds { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Reference price in USD (display fallback + spend-hook/reporting basis). The store's localized price wins on device.</summary>
        public decimal UsdReference { get; set; }
        public List<RewardGrant> Lines { get; set; } = new List<RewardGrant>();
        public StoreEffectDef Effect { get; set; }
        public StoreAvailabilityDef Availability { get; set; } = new StoreAvailabilityDef();

        /// <summary>Chips paid per USD of reference price — the number the value guard compares (0 for effect-only products).</summary>
        public decimal ChipsPerUsd()
        {
            if (UsdReference <= 0m || Lines == null) return 0m;
            decimal chips = Lines.Where(l => l != null && l.Kind == RewardKind.Currency && string.Equals(l.Id, "Chips", StringComparison.OrdinalIgnoreCase)).Sum(l => l.Amount);
            return chips / UsdReference;
        }
    }

    /// <summary>The catalog document (overlay shape). Admin-edited JSON in Redis <c>khela:store</c>; falls back to <see cref="StoreCatalog.Defaults"/>.</summary>
    public sealed class StoreCatalogConfig
    {
        public int Version { get; set; } = 1;
        /// <summary>Catalog-level switch (the runtime kill switch is <c>Store:Enabled</c> on the settings overlay; this is the authored one).</summary>
        public bool Enabled { get; set; } = true;
        public List<StoreSectionDef> Sections { get; set; } = new List<StoreSectionDef>();
        public List<StoreProductDef> Products { get; set; } = new List<StoreProductDef>();

        public StoreProductDef Find(string id)
            => string.IsNullOrWhiteSpace(id) ? null
             : Products?.FirstOrDefault(p => p != null && string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The store catalog: Redis key, JSON round-trip, the seed catalog (docs/IAP_SPEC.md §3.1) and the fail-closed
    /// validator the admin save and the service both run. Pure — no I/O — so it is unit-tested (StoreCatalogTests).
    /// </summary>
    public static class StoreCatalog
    {
        /// <summary>The Redis key holding the admin-edited catalog JSON (overlay; absent ⇒ code defaults). Travels in the settings seed file.</summary>
        public const string RedisKey = "khela:store";

        public const int MaxProductIdLength = 64;
        public const int MaxStoreIdLength = 128;
        public const int MaxSectionKeyLength = 32;
        public const int MaxTitleLength = 100;
        public const int MaxDescriptionLength = 500;
        public const int MaxBadgeLength = 32;
        public const int MaxImageUrlLength = 512;
        public const int MaxImages = RewardGrant.MaxImages;

        /// <summary>Effect types the server knows how to fulfil. The validator checks against this set (or the registered handlers, when given).</summary>
        public static readonly string[] BuiltInEffects = { EffectPiggyBreak, EffectGoldenPass, EffectVipBooster };
        public const string EffectPiggyBreak = "PiggyBreak";
        public const string EffectGoldenPass = "GoldenPass";
        public const string EffectVipBooster = "VipBooster";
        public const string PiggyTierParam = "tier";

        /// <summary>Store product ids: lowercase letters, digits, '_' and '.' (the intersection of what Play and the App Store accept).</summary>
        private static readonly Regex IdPattern = new Regex("^[a-z0-9_.]+$", RegexOptions.Compiled);

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        // ---------------------------------------------------------------- defaults (IAP_SPEC §3.1)

        /// <summary>
        /// The seed catalog — the numbers agreed on 2026-08-22: store minimums 5M/$1.99 and 10M/$2.99, value per dollar rising
        /// up the ladder but never above the piggy's ~5M/$ (the bank stays the best offer in the game); Kash 100/$1.99;
        /// piggy tiers 3 SKUs each; the golden pass; two VIP boosters. The real catalog is authored in the admin panel,
        /// which overrides this wholesale.
        /// </summary>
        public static StoreCatalogConfig Defaults()
        {
            var cfg = new StoreCatalogConfig
            {
                Version = 1,
                Enabled = true,
                Sections =
                {
                    new StoreSectionDef { Key = "chips", Title = "Chips", SortOrder = 10 },
                    new StoreSectionDef { Key = "kash",  Title = "Kash",  SortOrder = 20 },
                    new StoreSectionDef { Key = "packs", Title = "Packs", SortOrder = 30 },
                    new StoreSectionDef { Key = "daily", Title = "Daily", SortOrder = 40 },
                    new StoreSectionDef { Key = "piggy", Title = "Piggy", SortOrder = 50 },
                    new StoreSectionDef { Key = "pass",  Title = "Pass",  SortOrder = 60 },
                    new StoreSectionDef { Key = "vip",   Title = "VIP",   SortOrder = 70 },
                },
            };

            // Chips — value/$ rises from 2.5M/$ to 5.0M/$ (the whale anchor, = the piggy's value/$, never above it).
            cfg.Products.Add(Chips("chips_01", 10,   5_000_000m,   1.99m, 0,   "",           "Chip Stack"));
            cfg.Products.Add(Chips("chips_02", 20,  10_000_000m,   2.99m, 33,  "",           "Chip Pile"));
            cfg.Products.Add(Chips("chips_03", 30,  18_000_000m,   4.99m, 44,  "",           "Chip Case"));
            cfg.Products.Add(Chips("chips_04", 40,  40_000_000m,   9.99m, 60,  "POPULAR",    "Chip Vault"));
            cfg.Products.Add(Chips("chips_05", 50,  90_000_000m,  19.99m, 80,  "",           "Chip Bank"));
            cfg.Products.Add(Chips("chips_06", 60, 240_000_000m,  49.99m, 92,  "",           "Chip Fortune"));
            cfg.Products.Add(Chips("chips_07", 70, 500_000_000m,  99.99m, 100, "BEST VALUE", "Chip Empire"));

            // Kash — 1 Kash ≈ $0.02; modest premium-currency bonuses.
            cfg.Products.Add(Kash("kash_01", 10,   100m,  1.99m, 0,  "",           "Kash Pouch"));
            cfg.Products.Add(Kash("kash_02", 20,   275m,  4.99m, 10, "",           "Kash Purse"));
            cfg.Products.Add(Kash("kash_03", 30,   600m,  9.99m, 20, "",           "Kash Case"));
            cfg.Products.Add(Kash("kash_04", 40, 1_300m, 19.99m, 29, "POPULAR",    "Kash Chest"));
            cfg.Products.Add(Kash("kash_05", 50, 3_500m, 49.99m, 39, "",           "Kash Vault"));
            cfg.Products.Add(Kash("kash_06", 60, 7_500m, 99.99m, 49, "BEST VALUE", "Kash Treasury"));

            // Starter pack — a template, shipped DISABLED until the offer is decided (one per player, real money).
            cfg.Products.Add(new StoreProductDef
            {
                Id = "starter_pack", Enabled = false, ProductType = StoreProductType.Consumable, Section = "packs", SortOrder = 10,
                Title = "Starter Pack", Description = "One-time welcome offer", Badge = "ONE TIME", BonusPercent = 150,
                StoreIds = BothStores("starter_pack"), UsdReference = 2.99m,
                Lines = { RewardGrant.Currency("Chips", 25_000_000m), RewardGrant.Currency("Kash", 50m) },
                Availability = new StoreAvailabilityDef { MaxPerUser = 1 },
            });

            // Piggy — 3 SKUs per tier (Full / ×2 / Early). The payout comes from the player's bank (tier resolved at redeem),
            // so these carry no lines. Early = the same chips the bank holds, at a premium for not waiting.
            AddPiggyTier(cfg, 1, 10,  1.99m,  3.99m,  2.99m);
            AddPiggyTier(cfg, 2, 20,  3.99m,  7.99m,  5.99m);
            AddPiggyTier(cfg, 3, 30,  7.99m, 14.99m, 11.99m);
            AddPiggyTier(cfg, 4, 40, 19.99m, 39.99m, 29.99m);

            // Golden pass — a real auto-renewing monthly subscription.
            cfg.Products.Add(new StoreProductDef
            {
                Id = "golden_pass", ProductType = StoreProductType.Subscription, Section = "pass", SortOrder = 10,
                Title = "Golden Pass", Description = "Monthly — unlocks the golden track", StoreIds = BothStores("golden_pass"),
                UsdReference = 4.99m, Effect = new StoreEffectDef { Type = EffectGoldenPass, Arg = "monthly" },
            });

            // VIP boosters (Progression Spec §3.6).
            cfg.Products.Add(new StoreProductDef
            {
                Id = "vip_booster_time", Section = "vip", SortOrder = 10, Title = "VIP Booster: Time", Description = "Keep your VIP level another month",
                StoreIds = BothStores("vip_booster_time"), UsdReference = 1.99m, Effect = new StoreEffectDef { Type = EffectVipBooster, Arg = "Time" },
            });
            cfg.Products.Add(new StoreProductDef
            {
                Id = "vip_booster_level", Section = "vip", SortOrder = 20, Title = "VIP Booster: Level Up", Description = "+1 VIP level",
                StoreIds = BothStores("vip_booster_level"), UsdReference = 9.99m, Effect = new StoreEffectDef { Type = EffectVipBooster, Arg = "LevelUp" },
            });

            return cfg;
        }

        private static StoreProductDef Chips(string id, int sort, decimal chips, decimal usd, int bonus, string badge, string title) => new StoreProductDef
        {
            Id = id, Section = "chips", SortOrder = sort, Title = title, Badge = badge, BonusPercent = bonus,
            StoreIds = BothStores(id), UsdReference = usd, Lines = { RewardGrant.Currency("Chips", chips) },
        };

        private static StoreProductDef Kash(string id, int sort, decimal kash, decimal usd, int bonus, string badge, string title) => new StoreProductDef
        {
            Id = id, Section = "kash", SortOrder = sort, Title = title, Badge = badge, BonusPercent = bonus,
            StoreIds = BothStores(id), UsdReference = usd, Lines = { RewardGrant.Currency("Kash", kash) },
        };

        private static void AddPiggyTier(StoreCatalogConfig cfg, int tier, int sort, decimal full, decimal dbl, decimal early)
        {
            cfg.Products.Add(Piggy($"piggy_t{tier}_full",  sort,     tier, "Full",       full,  $"Piggy Bank T{tier}"));
            cfg.Products.Add(Piggy($"piggy_t{tier}_x2",    sort + 1, tier, "FullDouble", dbl,   $"Piggy Bank T{tier} ×2"));
            cfg.Products.Add(Piggy($"piggy_t{tier}_early", sort + 2, tier, "Early",      early, $"Piggy Bank T{tier} (early)"));
        }

        private static StoreProductDef Piggy(string id, int sort, int tier, string option, decimal usd, string title) => new StoreProductDef
        {
            Id = id, Section = "piggy", SortOrder = sort, Title = title, StoreIds = BothStores(id), UsdReference = usd,
            Effect = new StoreEffectDef
            {
                Type = EffectPiggyBreak, Arg = option,
                Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [PiggyTierParam] = tier.ToString(CultureInfo.InvariantCulture) },
            },
        };

        /// <summary>Store ids default to the product id on both stores (the convention; override per store when one forces a different id).</summary>
        public static Dictionary<string, string> BothStores(string id) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [StorePlatform.GooglePlay.ToString()] = id,
            [StorePlatform.AppStore.ToString()] = id,
        };

        // ---------------------------------------------------------------- JSON

        /// <summary>Serialize the way the admin editor and the Redis overlay store it (indented, enums as names). Round-trips through <see cref="TryParse"/>.</summary>
        public static string ToJson(StoreCatalogConfig cfg) => JsonSerializer.Serialize(cfg, JsonOptions);

        /// <summary>Parse an admin override; null if blank/invalid JSON (the caller falls back to defaults). Does NOT validate — pair with <see cref="Validate"/>.</summary>
        public static StoreCatalogConfig TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var cfg = JsonSerializer.Deserialize<StoreCatalogConfig>(json, JsonOptions);
                if (cfg?.Products == null) return null;   // zero products is legal — it just means "nothing on sale"
                foreach (var p in cfg.Products.Where(p => p != null))
                {
                    // Normalise dictionaries to case-insensitive keys ("googleplay" == "GooglePlay") without changing content.
                    p.StoreIds = new Dictionary<string, string>(p.StoreIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    if (p.Effect?.Params != null)
                        p.Effect.Params = new Dictionary<string, string>(p.Effect.Params, StringComparer.OrdinalIgnoreCase);
                    p.Lines ??= new List<RewardGrant>();
                    p.Images ??= new List<string>();
                    p.Availability ??= new StoreAvailabilityDef();
                }
                cfg.Sections ??= new List<StoreSectionDef>();
                return cfg;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- lookups

        /// <summary>The store-product id this product is sold under on <paramref name="platform"/>; null = not sold there.
        /// The Fake store sells every enabled product under our own id, so the Editor loop covers the whole catalog.</summary>
        public static string StoreIdFor(StoreProductDef product, StorePlatform platform)
        {
            if (product == null) return null;
            if (platform == StorePlatform.Fake) return product.Id;
            if (platform == StorePlatform.Unknown) return null;
            if (product.StoreIds != null && product.StoreIds.TryGetValue(platform.ToString(), out var id) && !string.IsNullOrWhiteSpace(id))
                return id.Trim();
            return null;
        }

        /// <summary>Reverse lookup: which product is sold under <paramref name="storeId"/> on <paramref name="platform"/>.</summary>
        public static StoreProductDef ResolveByStoreId(StoreCatalogConfig cfg, StorePlatform platform, string storeId)
        {
            if (cfg?.Products == null || string.IsNullOrWhiteSpace(storeId)) return null;
            storeId = storeId.Trim();
            return cfg.Products.FirstOrDefault(p => p != null && string.Equals(StoreIdFor(p, platform), storeId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The best chips-per-USD among enabled chip products — the bar a piggy offer must clear (value guard).</summary>
        public static decimal BestChipsPerUsd(StoreCatalogConfig cfg)
            => cfg?.Products == null ? 0m : cfg.Products.Where(p => p != null && p.Enabled).Select(p => p.ChipsPerUsd()).DefaultIfEmpty(0m).Max();

        /// <summary>Piggy tier a PiggyBreak product belongs to (from <c>Params["tier"]</c>); 0 when absent/invalid.</summary>
        public static int PiggyTierOf(StoreProductDef product)
        {
            var s = product?.Effect?.Param(PiggyTierParam);
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 0;
        }

        public static bool TryParsePiggyOption(string arg, out Khela.Common.Piggy.PiggyBreakOption option)
            => Enum.TryParse(arg ?? "", ignoreCase: true, out option) && Enum.IsDefined(typeof(Khela.Common.Piggy.PiggyBreakOption), option);

        public static bool TryParseVipBooster(string arg, out Khela.Game.Services.Vip.VipBoosterKind kind)
            => Enum.TryParse(arg ?? "", ignoreCase: true, out kind) && Enum.IsDefined(typeof(Khela.Game.Services.Vip.VipBoosterKind), kind);

        // ---------------------------------------------------------------- validation (fail closed)

        /// <summary>
        /// Validate a catalog for the admin save / the effective-config load. Returns the FIRST error message, or null if valid.
        /// <paramref name="effectTypes"/> = the effect types with a registered grant handler (defaults to <see cref="BuiltInEffects"/>).
        /// <paramref name="allowRandomPayloads"/> = whether chest (random) lines may be sold for real money (<c>Store:AllowRandomPayloads</c>).
        /// </summary>
        public static string Validate(StoreCatalogConfig cfg, ISet<string> effectTypes = null, bool allowRandomPayloads = false)
        {
            if (cfg == null) return "Empty catalog.";
            if (cfg.Version < 1) return "Version must be ≥ 1.";
            if (cfg.Products == null) return "Products missing.";
            effectTypes ??= new HashSet<string>(BuiltInEffects, StringComparer.OrdinalIgnoreCase);

            var sectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in cfg.Sections ?? new List<StoreSectionDef>())
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Key)) return "A section has no key.";
                if (s.Key.Length > MaxSectionKeyLength) return $"Section '{s.Key}': key longer than {MaxSectionKeyLength}.";
                if (!sectionKeys.Add(s.Key.Trim())) return $"Section '{s.Key}' is listed twice.";
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var storeIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);   // platform → ids
            foreach (var p in cfg.Products)
            {
                if (p == null) return "A product entry is null.";
                if (string.IsNullOrWhiteSpace(p.Id)) return "A product has no id.";
                var id = p.Id.Trim();
                if (id.Length > MaxProductIdLength) return $"{id}: id longer than {MaxProductIdLength}.";
                if (!IdPattern.IsMatch(id)) return $"{id}: ids may only contain a-z, 0-9, '_' and '.'.";
                if (!ids.Add(id)) return $"{id}: duplicate product id.";
                if (!Enum.IsDefined(typeof(StoreProductType), p.ProductType)) return $"{id}: unknown product type.";
                if (sectionKeys.Count > 0 && !string.IsNullOrWhiteSpace(p.Section) && !sectionKeys.Contains(p.Section.Trim()))
                    return $"{id}: unknown section '{p.Section}'.";
                if ((p.Title ?? "").Length > MaxTitleLength) return $"{id}: title longer than {MaxTitleLength}.";
                if ((p.Description ?? "").Length > MaxDescriptionLength) return $"{id}: description longer than {MaxDescriptionLength}.";
                if ((p.Badge ?? "").Length > MaxBadgeLength) return $"{id}: badge longer than {MaxBadgeLength}.";
                if (p.BonusPercent < 0) return $"{id}: bonus % can't be negative.";
                if (p.UsdReference < 0m) return $"{id}: reference price can't be negative.";
                if (p.Images != null)
                {
                    if (p.Images.Count > MaxImages) return $"{id}: more than {MaxImages} images.";
                    if (p.Images.Any(u => u != null && u.Length > MaxImageUrlLength)) return $"{id}: an image url is longer than {MaxImageUrlLength}.";
                }

                // store ids: known platforms, sane length, unique per platform
                if (p.StoreIds != null)
                {
                    foreach (var kv in p.StoreIds)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Value)) continue;   // empty = not sold there
                        if (!Enum.TryParse(kv.Key, ignoreCase: true, out StorePlatform platform) || platform == StorePlatform.Unknown || platform == StorePlatform.Fake)
                            return $"{id}: unknown store platform '{kv.Key}'.";
                        var sid = kv.Value.Trim();
                        if (sid.Length > MaxStoreIdLength) return $"{id}: store id for {platform} longer than {MaxStoreIdLength}.";
                        if (!IdPattern.IsMatch(sid)) return $"{id}: store id '{sid}' may only contain a-z, 0-9, '_' and '.'.";
                        if (!storeIds.TryGetValue(platform.ToString(), out var set)) storeIds[platform.ToString()] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (!set.Add(sid)) return $"{id}: store id '{sid}' is used by another product on {platform}.";
                    }
                }

                // what it pays
                var lines = p.Lines ?? new List<RewardGrant>();
                foreach (var line in lines)
                {
                    if (line == null) return $"{id}: a reward line is null.";
                    if (!Enum.IsDefined(typeof(RewardKind), line.Kind)) return $"{id}: unknown reward kind.";
                    switch (line.Kind)
                    {
                        case RewardKind.Currency:
                            if (!RewardCurrencies.TryParse(line.Id, out var currency)) return $"{id}: '{line.Id}' is not a currency name.";
                            if (!RewardCurrencies.IsAllowed(currency)) return $"{id}: {currency} may never be sold (allowed: {RewardCurrencies.AllowedList}).";
                            if (line.Amount <= 0m) return $"{id}: {currency} amount must be > 0.";
                            break;
                        case RewardKind.Xp:
                            if (line.Amount <= 0m) return $"{id}: XP amount must be > 0.";
                            break;
                        case RewardKind.Chest:
                            if (!allowRandomPayloads) return $"{id}: random (chest) payloads may not be sold for real money (Store:AllowRandomPayloads is off).";
                            if (!RewardIds.TryParseChest(line.Id, out _, out _)) return $"{id}: '{line.Id}' is not a chest id (key:tier).";
                            break;
                        default:
                            if (string.IsNullOrWhiteSpace(line.Id)) return $"{id}: {line.Kind} line needs an id.";
                            if (line.Amount < 1m) return $"{id}: {line.Kind} amount must be ≥ 1.";
                            break;
                    }
                }

                // effect
                if (p.Effect != null)
                {
                    if (string.IsNullOrWhiteSpace(p.Effect.Type)) return $"{id}: effect has no type.";
                    if (!effectTypes.Contains(p.Effect.Type.Trim())) return $"{id}: no grant handler for effect '{p.Effect.Type}'.";
                    switch (p.Effect.Type.Trim())
                    {
                        case EffectPiggyBreak:
                            if (!TryParsePiggyOption(p.Effect.Arg, out _)) return $"{id}: PiggyBreak needs arg Full | FullDouble | Early.";
                            if (PiggyTierOf(p) <= 0) return $"{id}: PiggyBreak needs params.tier ≥ 1.";
                            break;
                        case EffectGoldenPass:
                            if (string.IsNullOrWhiteSpace(p.Effect.Arg)) return $"{id}: GoldenPass needs the pass key as arg.";
                            if (p.ProductType != StoreProductType.Subscription) return $"{id}: GoldenPass must be a Subscription product.";
                            break;
                        case EffectVipBooster:
                            if (!TryParseVipBooster(p.Effect.Arg, out _)) return $"{id}: VipBooster needs arg Time | LevelUp.";
                            break;
                    }
                }

                if (lines.Count == 0 && p.Effect == null) return $"{id}: grants nothing (no lines, no effect).";
                if (p.ProductType == StoreProductType.Subscription && p.Effect == null) return $"{id}: a subscription must carry an effect.";

                var a = p.Availability ?? new StoreAvailabilityDef();
                if (a.MaxPerUser < 0 || a.MaxPerUserPerDay < 0 || a.MinLevel < 0) return $"{id}: availability limits can't be negative.";
                if (a.FromUtc.HasValue && a.ToUtc.HasValue && a.ToUtc <= a.FromUtc) return $"{id}: availability window ends before it starts.";
            }
            return null;
        }

        /// <summary>Enabled products with no store id on an enabled platform — a WARNING for the admin (authoring ahead of the consoles is normal), never an error.</summary>
        public static List<string> MissingStoreIds(StoreCatalogConfig cfg, IEnumerable<StorePlatform> enabledPlatforms)
        {
            var result = new List<string>();
            if (cfg?.Products == null) return result;
            var platforms = (enabledPlatforms ?? Array.Empty<StorePlatform>()).Where(p => p != StorePlatform.Fake && p != StorePlatform.Unknown).ToList();
            foreach (var p in cfg.Products.Where(p => p != null && p.Enabled))
                foreach (var platform in platforms)
                    if (StoreIdFor(p, platform) == null) result.Add($"{p.Id}: no store id for {platform}");
            return result;
        }

        /// <summary>Value guard (PIGGY_BANK_SPEC §6): a piggy offer whose chips-per-USD is WORSE than the best chip pack is a warning
        /// — the bank is meant to be the best offer in the game because it is earned. Returns the offending offers' ids.</summary>
        public static List<string> PiggyValueWarnings(StoreCatalogConfig cfg, IEnumerable<(string ProductId, decimal Chips)> piggyOffers)
        {
            var result = new List<string>();
            if (cfg == null || piggyOffers == null) return result;
            var best = BestChipsPerUsd(cfg);
            foreach (var (productId, chips) in piggyOffers)
            {
                var p = cfg.Find(productId);
                if (p == null || p.UsdReference <= 0m || chips <= 0m) continue;
                var perUsd = chips / p.UsdReference;
                if (perUsd < best)
                    result.Add($"{productId}: {perUsd:N0} chips/$ is worse than the best chip pack ({best:N0} chips/$)");
            }
            return result;
        }
    }
}
