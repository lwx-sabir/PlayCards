using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Khela.Game.Database.Models;
using Khela.Game.Services.Rewards;

namespace Khela.Game.Services.Chests
{
    /// <summary>Chest rarity. Higher tiers usually roll bigger reward ranges.</summary>
    public enum ChestTier { Common, Uncommon, Rare }

    /// <summary>A min/max range for one currency in a chest. On open, a uniform random amount in [Min, Max] (inclusive)
    /// is rolled (deterministically per open, so it's retry-safe) and credited.</summary>
    public sealed class ChestRewardRange
    {
        public CurrencyType Currency { get; set; }
        public long Min { get; set; }
        public long Max { get; set; }
    }

    /// <summary>
    /// A chest definition — admin-creatable. Identified by (<see cref="Key"/>, <see cref="Tier"/>). Carries display
    /// metadata (title/description/icon) and the per-currency reward ranges it rolls. "Any reward type" = any wallet
    /// currency EXCEPT the tradeable token (<see cref="CurrencyType.Tokens"/>), which a chest may never grant.
    /// </summary>
    public sealed class ChestDef
    {
        public string Key { get; set; }          // chest family id, e.g. "CK_Chest" (admin-defined)
        public ChestTier Tier { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string IconKey { get; set; }      // optional client sprite key (chest icon)
        public List<ChestRewardRange> Rewards { get; set; } = new List<ChestRewardRange>();
    }

    /// <summary>The chest catalog (overlay shape). Admin-editable as JSON in Redis <c>khela:chests</c>; falls back to
    /// <see cref="ChestCatalog.Defaults"/>.</summary>
    public sealed class ChestConfig
    {
        public List<ChestDef> Chests { get; set; } = new List<ChestDef>();

        public ChestDef Find(string key, ChestTier tier)
            => string.IsNullOrEmpty(key) ? null
             : Chests?.FirstOrDefault(c => string.Equals(c.Key, key, System.StringComparison.OrdinalIgnoreCase) && c.Tier == tier);
    }

    public static class ChestCatalog
    {
        public const string RedisKey = "khela:chests";

        /// <summary>The ONLY currencies a chest may grant (allowlist — fail-CLOSED). The tradeable token, any undefined
        /// currency int, and any FUTURE non-listed currency are all rejected by construction, so a typo or an appended
        /// enum value can never re-open the legal guardrail (CLAUDE.md NON-NEGOTIABLE #2/#4). Enforced on save
        /// (<see cref="Validate"/>) AND at open (<see cref="RollRewards"/>). Delegates to
        /// <see cref="RewardCurrencies"/> — ONE allowlist for every reward system, so they can't drift apart.</summary>
        public static bool IsAllowedReward(CurrencyType c) => RewardCurrencies.IsGrantable(c);

        /// <summary>The tradeable token specifically — kept only for a precise error message (the headline forbidden
        /// case). The actual gate is the <see cref="IsAllowedReward"/> allowlist.</summary>
        public static bool IsForbidden(CurrencyType c) => RewardCurrencies.IsForbidden(c);

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>Built-in chests. CK_Chest rolls Chips + Kash; ranges grow by tier. (Modest defaults — tune later.)</summary>
        public static ChestConfig Defaults() => new ChestConfig
        {
            Chests = new List<ChestDef>
            {
                new ChestDef
                {
                    Key = "CK_Chest", Tier = ChestTier.Common, IconKey = "chest_ck",
                    Title = "Chips & Kash Chest", Description = "A small mix of chips and Kash.",
                    Rewards = new List<ChestRewardRange>
                    {
                        new ChestRewardRange { Currency = CurrencyType.Chips, Min = 2000,  Max = 8000 },
                        new ChestRewardRange { Currency = CurrencyType.Kash,  Min = 5,     Max = 20 },
                    },
                },
                new ChestDef
                {
                    Key = "CK_Chest", Tier = ChestTier.Uncommon, IconKey = "chest_ck",
                    Title = "Chips & Kash Chest", Description = "A solid mix of chips and Kash.",
                    Rewards = new List<ChestRewardRange>
                    {
                        new ChestRewardRange { Currency = CurrencyType.Chips, Min = 8000,  Max = 25000 },
                        new ChestRewardRange { Currency = CurrencyType.Kash,  Min = 20,    Max = 60 },
                    },
                },
                new ChestDef
                {
                    Key = "CK_Chest", Tier = ChestTier.Rare, IconKey = "chest_ck",
                    Title = "Chips & Kash Chest", Description = "A big mix of chips and Kash.",
                    Rewards = new List<ChestRewardRange>
                    {
                        new ChestRewardRange { Currency = CurrencyType.Chips, Min = 25000, Max = 75000 },
                        new ChestRewardRange { Currency = CurrencyType.Kash,  Min = 60,    Max = 200 },
                    },
                },
            },
        };

        public static ChestConfig TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var cfg = JsonSerializer.Deserialize<ChestConfig>(json, JsonOptions);
                return (cfg?.Chests != null && cfg.Chests.Count > 0) ? cfg : null;
            }
            catch { return null; }
        }

        /// <summary>Validate a parsed config for the admin save. Returns an error message, or null if valid.
        /// Enforces: non-empty unique (Key, Tier); each reward has a non-forbidden currency and 0 &lt;= Min &lt;= Max.</summary>
        public static string Validate(ChestConfig cfg)
        {
            if (cfg?.Chests == null || cfg.Chests.Count == 0) return "Add at least one chest.";
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var c in cfg.Chests)
            {
                if (string.IsNullOrWhiteSpace(c.Key)) return "Every chest needs a Key (e.g. \"CK_Chest\").";
                var id = $"{c.Key}|{c.Tier}";
                if (!seen.Add(id)) return $"Duplicate chest: {c.Key} / {c.Tier}.";
                if (c.Rewards == null || c.Rewards.Count == 0) return $"{c.Key} / {c.Tier}: add at least one reward.";
                foreach (var r in c.Rewards)
                {
                    if (!IsAllowedReward(r.Currency))
                        return IsForbidden(r.Currency)
                            ? $"{c.Key} / {c.Tier}: '{r.Currency}' can never be a chest reward (tradeable token)."
                            : $"{c.Key} / {c.Tier}: '{(int)r.Currency}' is not a permitted chest reward currency (allowed: {RewardCurrencies.GrantableList}).";
                    if (r.Min < 0 || r.Max < 0) return $"{c.Key} / {c.Tier}: amounts can't be negative.";
                    if (r.Max < r.Min) return $"{c.Key} / {c.Tier}: Max ({r.Max}) is below Min ({r.Min}) for {r.Currency}.";
                }
            }
            return null;
        }

        /// <summary>
        /// The reward lines a chest rolls for a given open key — allowlist-FILTERED (fail-closed: a token / undefined /
        /// any non-permitted currency is dropped here, so a bad override that bypassed <see cref="Validate"/> — e.g. a
        /// raw Redis write — can never be credited) and DETERMINISTIC per <paramref name="idemKey"/> (same key → same
        /// amounts, retry-safe). Pure: no wallet, no Redis — directly unit-testable.
        /// </summary>
        public static List<(CurrencyType Currency, long Amount)> RollRewards(ChestDef def, string idemKey)
        {
            var list = new List<(CurrencyType, long)>();
            if (def?.Rewards == null) return list;
            foreach (var r in def.Rewards)
            {
                if (!IsAllowedReward(r.Currency)) continue;     // fail-closed legal guardrail
                long amount = Roll(idemKey, r.Currency, r.Min, r.Max);
                if (amount > 0) list.Add((r.Currency, amount));
            }
            return list;
        }

        // Deterministic uniform amount in [min, max] from (idemKey, currency) — same inputs → same amount (retry-safe).
        internal static long Roll(string idemKey, CurrencyType currency, long min, long max)
        {
            if (max <= min) return min < 0 ? 0 : min;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{idemKey}:{(int)currency}"));
            ulong h = BitConverter.ToUInt64(hash, 0);
            ulong range = (ulong)(max - min) + 1UL;
            return min + (long)(h % range);
        }
    }
}
