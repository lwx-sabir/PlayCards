using System;
using System.Collections.Generic;
using System.Globalization;

namespace Khela.Game.Services.Vip
{
    /// <summary>
    /// Tunable VIP knobs (Progression Spec §3 + §3.6). Scalars are runtime-overridable via the admin "khela:settings"
    /// hash (see <see cref="Overlay"/>); the per-tier / per-level ARRAYS are code/appsettings defaults. Tier arrays are
    /// indexed by (int)VipTier [0]None…[7]BlackDiamond; VIP-Level arrays by level [0]none…[10]. Numbers are STARTING
    /// DEFAULTS — calibrate against telemetry. The comp multiplier (TierBonus + VipLevelBonus) applies to the
    /// Loyalty/store/faucet tracks ONLY, NEVER to winnings.
    /// </summary>
    public sealed class VipConfig
    {
        public bool Enabled { get; init; } = true;
        public int VipEntryLevel { get; init; } = 20;
        public int TierWindowMonths { get; init; } = 12;
        public int BadgeWindowDays { get; init; } = 30;
        public decimal SpChipsPerPoint { get; init; } = 50m;
        public decimal SpPerUsd { get; init; } = 100m;
        public long SpFromWagerDailyCap { get; init; } = 100_000;
        public decimal DemoteHysteresis { get; init; } = 0.85m;
        public int VipMaintainDays { get; init; } = 30;   // a settled round / LP / IAP top-up within this window holds the VIP level
        public int VipBoosterTimeDays { get; init; } = 30; // days the cheap "VIP Booster: Time" IAP item adds to the current level

        // --- Tier (rank ladder, §3.0–3.5) ---
        public long[] SpThresholds      { get; init; } = new long[]    { 0, 0, 50_000, 250_000, 1_250_000, 6_250_000, 31_000_000, 150_000_000 };
        // Spend floors are ZERO now: money buys VIP-P, not status (docs/VIP_SPEC.md §2). The mechanism stays so a spend
        // gate can be re-imposed from the admin, and it is sourced from StorePurchases — the actual record of spend —
        // rather than the retired SP ledger.
        public decimal[] SpendFloorsUsd { get; init; } = new decimal[] { 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m };
        /// <summary>
        /// Where each tier lands at the SEASON ROLL, indexed by the tier climbed to. A season resets you to a LOWER tier —
        /// far enough down that the climb is worth making again, not so far that a season of play counts for nothing —
        /// and your SP to that tier's bar. Bronze (the free floor) and None stay put.
        /// </summary>
        public int[] TierResetTo { get; init; } = new int[] { 0, 1, 1, 2, 3, 4, 5, 6 };
        // Small comp bonus per tier (the "experienced player" signal): Bronze +1% … Black Diamond +15%.
        public decimal[] TierBonusPct   { get; init; } = new decimal[] { 0m, 0.01m, 0.03m, 0.05m, 0.08m, 0.11m, 0.13m, 0.15m };
        // VIP-Level grind acceleration per tier — a higher tier grinds VIP Level faster (Bronze ×1 → Black ×3).
        public decimal[] TierFactors    { get; init; } = new decimal[] { 0m, 1.0m, 1.2m, 1.5m, 1.9m, 2.3m, 2.6m, 3.0m };

        // --- VIP Level (premium ladder, §3.6) ---
        // Big comp bonus per VIP level (the real lever): VIP 1 +20% … VIP 10 +170%.
        public decimal[] VipLevelBonusPct { get; init; } = new decimal[] { 0m, 0.20m, 0.35m, 0.50m, 0.68m, 0.88m, 1.08m, 1.28m, 1.45m, 1.60m, 1.70m };
        // Into-level grind needed to reach level n from n-1 (index 1..10; [0] unused). HUGE — even VIP 1 is a long road.
        public long[] VipLevelThresholds  { get; init; } = new long[]    { 0, 5_000_000, 6_000_000, 7_500_000, 9_000_000, 11_000_000, 13_000_000, 16_000_000, 19_000_000, 23_000_000, 28_000_000 };
        // LP cost to MAINTAIN a level for one period (index 1..10; [0] unused).
        public long[] VipMaintainLpCost   { get; init; } = new long[]    { 0, 500, 800, 1_200, 1_800, 2_600, 3_600, 5_000, 7_000, 9_500, 13_000 };

        /// <summary>Overlay admin "Vip:*" runtime overrides onto a base config — SCALARS only; arrays pass through.
        /// Lenient parse; missing/bad keeps the base value. <see cref="Enabled"/> is intentionally NOT overridable.</summary>
        public static VipConfig Overlay(VipConfig b, IReadOnlyDictionary<string, string> o)
        {
            if (o == null || o.Count == 0) return b;
            var tiers = ParseTiers(o.TryGetValue("Vip:Tiers", out var tiersJson) ? tiersJson : null, b);
            var levels = ParseLevels(o.TryGetValue("Vip:Levels", out var levelsJson) ? levelsJson : null, b);
            return new VipConfig
            {
                Enabled             = b.Enabled,
                VipEntryLevel       = Int(o, "Vip:VipEntryLevel", b.VipEntryLevel),
                TierWindowMonths    = Int(o, "Vip:TierWindowMonths", b.TierWindowMonths),
                BadgeWindowDays     = Int(o, "Vip:BadgeWindowDays", b.BadgeWindowDays),
                SpChipsPerPoint     = Dec(o, "Vip:SpChipsPerPoint", b.SpChipsPerPoint),
                SpPerUsd            = Dec(o, "Vip:SpPerUsd", b.SpPerUsd),
                SpFromWagerDailyCap = Lng(o, "Vip:SpFromWagerDailyCap", b.SpFromWagerDailyCap),
                DemoteHysteresis    = Dec(o, "Vip:DemoteHysteresis", b.DemoteHysteresis),
                VipMaintainDays     = Int(o, "Vip:VipMaintainDays", b.VipMaintainDays),
                VipBoosterTimeDays  = Int(o, "Vip:VipBoosterTimeDays", b.VipBoosterTimeDays),
                // The two LADDERS travel as one JSON document each (Vip:Tiers, Vip:Levels) rather than seven parallel
                // array keys: a tier's SP bar, spend floor, comp bonus and grind factor are ONE row of one decision, and
                // seven keys that could be saved at different lengths is how a ladder ends up half-retuned.
                SpThresholds = tiers.SpThresholds, SpendFloorsUsd = tiers.SpendFloorsUsd,
                TierBonusPct = tiers.TierBonusPct, TierFactors = tiers.TierFactors, TierResetTo = tiers.TierResetTo,
                VipLevelBonusPct = levels.VipLevelBonusPct, VipLevelThresholds = levels.VipLevelThresholds,
                VipMaintainLpCost = levels.VipMaintainLpCost,
            };
        }

        // ---------------------------------------------------------------- ladders (admin-editable)

        /// <summary>One tier's row in the admin's tier table — index 0 = None, 7 = BlackDiamond.</summary>
        public sealed class TierRow
        {
            public long SpThreshold { get; init; }
            public decimal SpendFloorUsd { get; init; }
            /// <summary>
            /// The tier this one falls to at the season roll (0 None … 7 Black Diamond). NULLABLE on purpose: a ladder saved
            /// before seasons existed has no such field, and a non-nullable int would read as 0 — every tier resetting to
            /// None, so the first roll would wipe every player to no badge and no SP. Null means "keep the built-in rung".
            /// </summary>
            public int? ResetTo { get; init; }
            /// <summary>Comp bonus as a FRACTION (0.15 = +15%).</summary>
            public decimal BonusPct { get; init; }
            /// <summary>VIP-Level grind multiplier at this tier (0 = earns none).</summary>
            public decimal Factor { get; init; }
        }

        /// <summary>One VIP level's row — index 0 of the saved list is level 1.</summary>
        public sealed class LevelRow
        {
            /// <summary>Grind needed to reach this level from the one below.</summary>
            public long Threshold { get; init; }
            /// <summary>Comp bonus as a FRACTION (1.70 = +170%).</summary>
            public decimal BonusPct { get; init; }
            public long MaintainLp { get; init; }
        }

        private static readonly System.Text.Json.JsonSerializerOptions LadderJson = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>The tier ladder as the admin table shows it (always 8 rows, None…BlackDiamond).</summary>
        public static List<TierRow> TierRows(VipConfig c)
        {
            var rows = new List<TierRow>(8);
            for (int i = 0; i < 8; i++)
                rows.Add(new TierRow
                {
                    SpThreshold = At(c.SpThresholds, i), SpendFloorUsd = At(c.SpendFloorsUsd, i),
                    BonusPct = At(c.TierBonusPct, i), Factor = At(c.TierFactors, i),
                    ResetTo = AtInt(c.TierResetTo, i),
                });
            return rows;
        }

        /// <summary>The VIP-level ladder as the admin table shows it — one row per level 1…N (index 0 = level 1).</summary>
        public static List<LevelRow> LevelRows(VipConfig c)
        {
            int n = Math.Max(0, (c.VipLevelThresholds?.Length ?? 1) - 1);
            var rows = new List<LevelRow>(n);
            for (int lvl = 1; lvl <= n; lvl++)
                rows.Add(new LevelRow
                {
                    Threshold = At(c.VipLevelThresholds, lvl), BonusPct = At(c.VipLevelBonusPct, lvl),
                    MaintainLp = At(c.VipMaintainLpCost, lvl),
                });
            return rows;
        }

        public static string SerializeTiers(IEnumerable<TierRow> rows) => System.Text.Json.JsonSerializer.Serialize(rows, LadderJson);
        public static string SerializeLevels(IEnumerable<LevelRow> rows) => System.Text.Json.JsonSerializer.Serialize(rows, LadderJson);

        /// <summary>
        /// Parse the tier ladder. Refused WHOLE — anything unusable falls back to <paramref name="fallback"/> rather than
        /// half-applying: a ladder with one bad rung is a ladder that ranks players wrongly, which is worse than one that
        /// is merely mistuned. Must be exactly 8 rows (the tier enum's width) and non-decreasing in SP.
        /// </summary>
        public static (long[] SpThresholds, decimal[] SpendFloorsUsd, decimal[] TierBonusPct, decimal[] TierFactors, int[] TierResetTo)
            ParseTiers(string json, VipConfig fallback)
        {
            var fb = (fallback.SpThresholds, fallback.SpendFloorsUsd, fallback.TierBonusPct, fallback.TierFactors, fallback.TierResetTo);
            if (string.IsNullOrWhiteSpace(json)) return fb;
            try
            {
                var rows = System.Text.Json.JsonSerializer.Deserialize<List<TierRow>>(json, LadderJson);
                if (rows == null || rows.Count != 8) return fb;
                var sp = new long[8]; var floors = new decimal[8]; var bonus = new decimal[8]; var factors = new decimal[8]; var resets = new int[8];
                for (int i = 0; i < 8; i++)
                {
                    var r = rows[i];
                    if (r == null || r.SpThreshold < 0 || r.SpendFloorUsd < 0m || r.BonusPct < 0m || r.Factor < 0m) return fb;
                    if (i > 0 && r.SpThreshold < sp[i - 1]) return fb;   // a ladder must not go down
                    sp[i] = r.SpThreshold; floors[i] = r.SpendFloorUsd; bonus[i] = r.BonusPct; factors[i] = r.Factor;
                    // A reset must go DOWN (or stay): a season that promotes you for finishing it is not a reset. A row
                    // saved BEFORE seasons existed has no such field — null keeps the built-in rung, because reading a
                    // missing value as 0 would mean every tier resets to None and the first roll would wipe every badge.
                    var reset = r.ResetTo ?? AtInt(fallback.TierResetTo, i);
                    if (reset < 0 || reset > i) return fb;
                    resets[i] = reset;
                }
                // Tier None must grind NOTHING. The web form refuses this too, but a Vip:Tiers document can also arrive
                // from a seed file or a direct Redis edit, and a non-zero factor there would let every player below the
                // VIP entry level grind VIP Levels — the gate the whole ladder rests on. The GAME is the one that guarantees it.
                if (factors[0] != 0m) return fb;
                return (sp, floors, bonus, factors, resets);
            }
            catch { return fb; }
        }

        /// <summary>Parse the VIP-level ladder (one row per level 1…N). Refused whole; every threshold must be &gt; 0.</summary>
        public static (decimal[] VipLevelBonusPct, long[] VipLevelThresholds, long[] VipMaintainLpCost)
            ParseLevels(string json, VipConfig fallback)
        {
            var fb = (fallback.VipLevelBonusPct, fallback.VipLevelThresholds, fallback.VipMaintainLpCost);
            if (string.IsNullOrWhiteSpace(json)) return fb;
            try
            {
                var rows = System.Text.Json.JsonSerializer.Deserialize<List<LevelRow>>(json, LadderJson);
                if (rows == null || rows.Count == 0) return fb;
                int n = rows.Count;
                var bonus = new decimal[n + 1]; var thresholds = new long[n + 1]; var maintain = new long[n + 1];
                for (int i = 0; i < n; i++)
                {
                    var r = rows[i];
                    // A threshold of 0 would let a level be reached for nothing and the level-up loop would spin.
                    if (r == null || r.Threshold <= 0L || r.BonusPct < 0m || r.MaintainLp < 0L) return fb;
                    thresholds[i + 1] = r.Threshold; bonus[i + 1] = r.BonusPct; maintain[i + 1] = r.MaintainLp;
                }
                return (bonus, thresholds, maintain);
            }
            catch { return fb; }
        }

        private static long At(long[] a, int i) => (a != null && i >= 0 && i < a.Length) ? a[i] : 0L;
        private static decimal At(decimal[] a, int i) => (a != null && i >= 0 && i < a.Length) ? a[i] : 0m;
        private static int AtInt(int[] a, int i) => (a != null && i >= 0 && i < a.Length) ? a[i] : 0;

        private static decimal Dec(IReadOnlyDictionary<string, string> o, string k, decimal d)
            => o.TryGetValue(k, out var v) && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : d;
        private static long Lng(IReadOnlyDictionary<string, string> o, string k, long d)
            => o.TryGetValue(k, out var v) && long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : d;
        private static int Int(IReadOnlyDictionary<string, string> o, string k, int d)
            => o.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : d;
    }
}
