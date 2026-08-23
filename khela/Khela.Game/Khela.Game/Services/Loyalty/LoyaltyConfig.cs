using System.Collections.Generic;
using System.Globalization;

namespace Khela.Game.Services.Loyalty
{
    /// <summary>One Loyalty-Store item (Progression Spec §4). <c>Kind="chips"</c> grants <c>ChipAmount</c> into the
    /// EARNED bucket via the wallet; other kinds are reserved (rejected at redeem for now). <c>MinVipTier</c> is the
    /// (int)VipTier gate (0 = none).</summary>
    public sealed class LoyaltyStoreItem
    {
        public string Id { get; init; } = "";
        public string Kind { get; init; } = "chips";
        public string Name { get; init; } = "";
        public long CostLp { get; init; }
        public decimal ChipAmount { get; init; }
        public int MinVipTier { get; init; } = 0;
    }

    /// <summary>Tunable Loyalty knobs (Progression Spec §4). Scalars are runtime-overridable via the khela:settings
    /// hash; the catalog is a code/appsettings default. LP is the redeemable comp balance on UserProfile.LoyaltyPoints,
    /// earned as a small fraction of clean wager × the VIP benefit multiplier — deliberately low so the store stays
    /// aspirational. Prices/amounts are starting defaults — tune on telemetry.</summary>
    public sealed class LoyaltyConfig
    {
        public bool Enabled { get; init; } = true;
        public decimal LpChipsPerPoint { get; init; } = 100m;   // clean wager chips per 1 LP (~1% comp)
        public decimal LpPerUsd { get; init; } = 0m;            // IAP drip (dormant; 0 = off until IAP)

        public LoyaltyStoreItem[] Catalog { get; init; } = new[]
        {
            new LoyaltyStoreItem { Id = "chips_1k",   Name = "1,000 Chips",   Kind = "chips", CostLp = 10,  ChipAmount = 1_000m },
            new LoyaltyStoreItem { Id = "chips_10k",  Name = "10,000 Chips",  Kind = "chips", CostLp = 90,  ChipAmount = 10_000m },
            new LoyaltyStoreItem { Id = "chips_100k", Name = "100,000 Chips", Kind = "chips", CostLp = 800, ChipAmount = 100_000m },
        };

        public static LoyaltyConfig Overlay(LoyaltyConfig b, IReadOnlyDictionary<string, string> o)
        {
            if (o == null || o.Count == 0) return b;
            return new LoyaltyConfig
            {
                Enabled         = b.Enabled,
                LpChipsPerPoint = Dec(o, "Loyalty:LpChipsPerPoint", b.LpChipsPerPoint),
                LpPerUsd        = Dec(o, "Loyalty:LpPerUsd", b.LpPerUsd),
                Catalog         = ParseCatalog(o.TryGetValue("Loyalty:Catalog", out var json) ? json : null, b.Catalog),
            };
        }

        private static readonly System.Text.Json.JsonSerializerOptions CatalogJson = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        public static string SerializeCatalog(IEnumerable<LoyaltyStoreItem> items) => System.Text.Json.JsonSerializer.Serialize(items, CatalogJson);

        /// <summary>
        /// Parse the LP store. Refused WHOLE — a half-applied price list is a shop that sells one item at the new rate and
        /// the rest at the old. An item needs an id, a positive cost and a positive amount; anything else falls back.
        ///
        /// The redemption rate is the one number that decides what LP is WORTH: earning 1 LP per <see cref="LpChipsPerPoint"/>
        /// chips wagered and redeeming it for R chips is a comp of R / LpChipsPerPoint. At 100 chips/LP earned and 100
        /// chips/LP redeemed that is 100% of wager back — a loop, not a comp. Set it deliberately.
        /// </summary>
        public static LoyaltyStoreItem[] ParseCatalog(string json, LoyaltyStoreItem[] fallback)
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            try
            {
                var items = System.Text.Json.JsonSerializer.Deserialize<List<LoyaltyStoreItem>>(json, CatalogJson);
                if (items == null) return fallback;
                var good = new List<LoyaltyStoreItem>(items.Count);
                var ids = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var i in items)
                {
                    if (i == null || string.IsNullOrWhiteSpace(i.Id) || i.CostLp <= 0L || i.ChipAmount <= 0m || i.MinVipTier < 0) return fallback;
                    if (!ids.Add(i.Id.Trim())) return fallback;   // two items with one id: which one did the player buy?
                    good.Add(new LoyaltyStoreItem
                    {
                        Id = i.Id.Trim(), Kind = string.IsNullOrWhiteSpace(i.Kind) ? "chips" : i.Kind.Trim(),
                        Name = i.Name ?? "", CostLp = i.CostLp, ChipAmount = i.ChipAmount, MinVipTier = i.MinVipTier,
                    });
                }
                return good.Count == 0 ? fallback : good.ToArray();   // an empty list is "no shop", which reads as broken
            }
            catch { return fallback; }
        }

        private static decimal Dec(IReadOnlyDictionary<string, string> o, string k, decimal d)
            => o.TryGetValue(k, out var v) && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : d;
    }
}
