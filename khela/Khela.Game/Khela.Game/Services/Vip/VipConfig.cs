using System;
using System.Collections.Generic;
using System.Globalization;

namespace Khela.Game.Services.Vip
{
    /// <summary>
    /// Tunable VIP knobs (docs/VIP_SPEC.md §2 + §4). Scalars are runtime-overridable via the admin "khela:settings"
    /// hash (see <see cref="Overlay"/>); the two LADDERS travel as one JSON document each (Vip:Tiers, Vip:Levels).
    /// Tier arrays are indexed by (int)VipTier [0]None…[7]BlackDiamond; VIP-Level arrays by level [0]none…[N]. Numbers
    /// are STARTING DEFAULTS — calibrate against telemetry.
    ///
    /// Two ladders, two jobs. The TIER is seasonal status, climbed by PLAY (SP). The VIP LEVEL is the money track,
    /// reached only by VIP-P bought in the store, and it is the one that carries the win bonus / loss rebate. The comp
    /// multiplier (tier boost + VIP-level boost) applies to the Loyalty/store/faucet tracks ONLY — never to odds.
    /// </summary>
    public sealed class VipConfig
    {
        public bool Enabled { get; init; } = true;
        public int VipEntryLevel { get; init; } = 20;
        public int TierWindowMonths { get; init; } = 12;
        public int BadgeWindowDays { get; init; } = 30;
        public decimal SpChipsPerPoint { get; init; } = 50m;
        public long SpFromWagerDailyCap { get; init; } = 100_000;
        public decimal DemoteHysteresis { get; init; } = 0.85m;
        /// <summary>
        /// The trailing window the VIP level is read from (docs/VIP_SPEC.md §4): the level is the band of the VIP-P
        /// CREDITED in the last this-many days. Stop buying and the window empties — that emptying IS the decay.
        /// </summary>
        public int WindowDays { get; init; } = 90;
        public int VipMaintainDays { get; init; } = 30;    // days one LP maintain (the non-IAP "Time" booster) adds to the hold
        public int VipBoosterTimeDays { get; init; } = 30; // days the cheap "VIP Booster: Time" IAP item adds to the hold

        // --- Tier (seasonal status ladder, §2) ---
        public long[] SpThresholds      { get; init; } = new long[]    { 0, 0, 50_000, 250_000, 1_250_000, 6_250_000, 31_000_000, 150_000_000 };
        // Spend floors are ZERO: money buys VIP-P, not status (docs/VIP_SPEC.md §2). The mechanism stays so a spend
        // gate can be re-imposed from the admin, and it is sourced from StorePurchases — the actual record of spend.
        public decimal[] SpendFloorsUsd { get; init; } = new decimal[] { 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m };
        /// <summary>
        /// Where each tier lands at the SEASON ROLL, indexed by the tier climbed to. A season resets you to a LOWER tier —
        /// far enough down that the climb is worth making again, not so far that a season of play counts for nothing —
        /// and your SP to that tier's bar. Bronze (the free floor) and None stay put.
        /// </summary>
        public int[] TierResetTo { get; init; } = new int[] { 0, 1, 1, 2, 3, 4, 5, 6 };
        // Small comp bonus per tier (the "experienced player" signal): Bronze +1% … Black Diamond +15%.
        public decimal[] TierBonusPct   { get; init; } = new decimal[] { 0m, 0.01m, 0.03m, 0.05m, 0.08m, 0.11m, 0.13m, 0.15m };

        // --- VIP Level (the money ladder, §4) — index 1..N; [0] = "no VIP" and is always zero ---
        /// <summary>VIP-P credited within <see cref="WindowDays"/> to stand at this level. ~100 VIP-P per $1, so level 1
        /// is the Starter Pack and level 10 is a serious spender. Strictly increasing.</summary>
        public long[] VipPointsRequired  { get; init; } = new long[]    { 0, 300, 1_000, 2_500, 5_000, 10_000, 20_000, 40_000, 75_000, 150_000, 300_000 };
        // Big comp bonus per VIP level (the real lever): VIP 1 +20% … VIP 10 +170%.
        public decimal[] VipLevelBonusPct { get; init; } = new decimal[] { 0m, 0.20m, 0.35m, 0.50m, 0.68m, 0.88m, 1.08m, 1.28m, 1.45m, 1.60m, 1.70m };
        /// <summary>How long a level is HELD after the purchase that reached it (§4) — the buffer that keeps a player
        /// VIP while the window drains, so one slow month is not an instant demotion.</summary>
        public int[] VipHoldDays          { get; init; } = new int[]     { 0, 30, 30, 45, 45, 60, 60, 75, 75, 90, 90 };
        /// <summary>Fraction of the day's net WINNINGS credited back on the way home (§4). Dormant until the rebate ships.</summary>
        public decimal[] VipWinBonusPct   { get; init; } = new decimal[] { 0m, 0.015m, 0.020m, 0.025m, 0.030m, 0.035m, 0.040m, 0.045m, 0.050m, 0.055m, 0.060m };
        /// <summary>Fraction of the day's net LOSSES credited back (§4) — the line that makes a losing day still pay something.</summary>
        public decimal[] VipLossRebatePct { get; init; } = new decimal[] { 0m, 0.0075m, 0.010m, 0.0125m, 0.015m, 0.0175m, 0.020m, 0.0225m, 0.025m, 0.0275m, 0.030m };
        /// <summary>Per-day ceiling on the win bonus / rebate, in chips. 0 = the feature is OFF for that level (the
        /// exchange's convention) — NOT uncapped, so a blank row can never become an uncapped faucet.</summary>
        public decimal[] VipDailyCapChips { get; init; } = new decimal[] { 0m, 500_000m, 1_000_000m, 2_000_000m, 3_500_000m, 5_000_000m, 8_000_000m, 12_000_000m, 16_000_000m, 20_000_000m, 25_000_000m };
        // LP cost to extend the hold by VipMaintainDays (index 1..N; [0] unused) — the live, non-IAP keep-up.
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
                SpFromWagerDailyCap = Lng(o, "Vip:SpFromWagerDailyCap", b.SpFromWagerDailyCap),
                DemoteHysteresis    = Dec(o, "Vip:DemoteHysteresis", b.DemoteHysteresis),
                WindowDays          = Int(o, "Vip:WindowDays", b.WindowDays),
                VipMaintainDays     = Int(o, "Vip:VipMaintainDays", b.VipMaintainDays),
                VipBoosterTimeDays  = Int(o, "Vip:VipBoosterTimeDays", b.VipBoosterTimeDays),
                // The two LADDERS travel as one JSON document each (Vip:Tiers, Vip:Levels) rather than a dozen parallel
                // array keys: a level's VIP-P bar, comp %, win %, rebate %, hold and daily cap are ONE row of one
                // decision, and separate keys that could be saved at different lengths is how a ladder ends up half-retuned.
                SpThresholds = tiers.SpThresholds, SpendFloorsUsd = tiers.SpendFloorsUsd,
                TierBonusPct = tiers.TierBonusPct, TierResetTo = tiers.TierResetTo,
                VipPointsRequired = levels.VipPointsRequired, VipLevelBonusPct = levels.VipLevelBonusPct,
                VipHoldDays = levels.VipHoldDays, VipWinBonusPct = levels.VipWinBonusPct,
                VipLossRebatePct = levels.VipLossRebatePct, VipDailyCapChips = levels.VipDailyCapChips,
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
        }

        /// <summary>
        /// One VIP level's row — index 0 of the saved list is level 1. Everything the money ladder decides lives here.
        /// The columns added with the VIP-P redesign are NULLABLE for the same reason <see cref="TierRow.ResetTo"/> is:
        /// a ladder saved before them has no such field, and reading a missing value as 0 would silently set every
        /// level's bar to nothing (reaching the top for free) or every daily cap to off.
        /// </summary>
        public sealed class LevelRow
        {
            /// <summary>VIP-P credited inside the window to stand at this level.</summary>
            public long? PointsRequired { get; init; }
            /// <summary>Comp bonus as a FRACTION (1.70 = +170%).</summary>
            public decimal BonusPct { get; init; }
            public long MaintainLp { get; init; }
            /// <summary>Days the level is held after the purchase that reached it.</summary>
            public int? HoldDays { get; init; }
            /// <summary>Fraction of a winning day's net credited back.</summary>
            public decimal? WinBonusPct { get; init; }
            /// <summary>Fraction of a losing day's net credited back.</summary>
            public decimal? LossRebatePct { get; init; }
            /// <summary>Chips-per-day ceiling on the two lines above; 0 = OFF.</summary>
            public decimal? DailyCapChips { get; init; }
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
                    BonusPct = At(c.TierBonusPct, i), ResetTo = AtInt(c.TierResetTo, i),
                });
            return rows;
        }

        /// <summary>The VIP-level ladder as the admin table shows it — one row per level 1…N (index 0 = level 1).</summary>
        public static List<LevelRow> LevelRows(VipConfig c)
        {
            int n = TopLevel(c);
            var rows = new List<LevelRow>(n);
            for (int lvl = 1; lvl <= n; lvl++)
                rows.Add(new LevelRow
                {
                    PointsRequired = At(c.VipPointsRequired, lvl), BonusPct = At(c.VipLevelBonusPct, lvl),
                    MaintainLp = At(c.VipMaintainLpCost, lvl), HoldDays = AtInt(c.VipHoldDays, lvl),
                    WinBonusPct = At(c.VipWinBonusPct, lvl), LossRebatePct = At(c.VipLossRebatePct, lvl),
                    DailyCapChips = At(c.VipDailyCapChips, lvl),
                });
            return rows;
        }

        /// <summary>The top VIP level THIS ladder defines (the arrays are admin-editable, so it is not always 10).</summary>
        public static int TopLevel(VipConfig c) => Math.Max(0, (c?.VipPointsRequired?.Length ?? 1) - 1);

        public static string SerializeTiers(IEnumerable<TierRow> rows) => System.Text.Json.JsonSerializer.Serialize(rows, LadderJson);
        public static string SerializeLevels(IEnumerable<LevelRow> rows) => System.Text.Json.JsonSerializer.Serialize(rows, LadderJson);

        /// <summary>
        /// Parse the tier ladder. Refused WHOLE — anything unusable falls back to <paramref name="fallback"/> rather than
        /// half-applying: a ladder with one bad rung is a ladder that ranks players wrongly, which is worse than one that
        /// is merely mistuned. Must be exactly 8 rows (the tier enum's width) and non-decreasing in SP.
        /// </summary>
        public static (long[] SpThresholds, decimal[] SpendFloorsUsd, decimal[] TierBonusPct, int[] TierResetTo)
            ParseTiers(string json, VipConfig fallback)
        {
            var fb = (fallback.SpThresholds, fallback.SpendFloorsUsd, fallback.TierBonusPct, fallback.TierResetTo);
            if (string.IsNullOrWhiteSpace(json)) return fb;
            try
            {
                var rows = System.Text.Json.JsonSerializer.Deserialize<List<TierRow>>(json, LadderJson);
                if (rows == null || rows.Count != 8) return fb;
                var sp = new long[8]; var floors = new decimal[8]; var bonus = new decimal[8]; var resets = new int[8];
                for (int i = 0; i < 8; i++)
                {
                    var r = rows[i];
                    if (r == null || r.SpThreshold < 0 || r.SpendFloorUsd < 0m || r.BonusPct < 0m) return fb;
                    if (i > 0 && r.SpThreshold < sp[i - 1]) return fb;   // a ladder must not go down
                    sp[i] = r.SpThreshold; floors[i] = r.SpendFloorUsd; bonus[i] = r.BonusPct;
                    // A reset must go DOWN (or stay): a season that promotes you for finishing it is not a reset. A row
                    // saved BEFORE seasons existed has no such field — null keeps the built-in rung, because reading a
                    // missing value as 0 would mean every tier resets to None and the first roll would wipe every badge.
                    var reset = r.ResetTo ?? AtInt(fallback.TierResetTo, i);
                    if (reset < 0 || reset > i) return fb;
                    resets[i] = reset;
                }
                return (sp, floors, bonus, resets);
            }
            catch { return fb; }
        }

        /// <summary>
        /// Parse the VIP-level ladder (one row per level 1…N). Refused whole; VIP-P bars must be positive and strictly
        /// increasing — equal bars would make two levels the same purchase, and a zero bar would hand a paid level to
        /// every player who never spent a cent.
        /// </summary>
        public static (long[] VipPointsRequired, decimal[] VipLevelBonusPct, int[] VipHoldDays,
                       decimal[] VipWinBonusPct, decimal[] VipLossRebatePct, decimal[] VipDailyCapChips, long[] VipMaintainLpCost)
            ParseLevels(string json, VipConfig fallback)
        {
            var fb = (fallback.VipPointsRequired, fallback.VipLevelBonusPct, fallback.VipHoldDays,
                      fallback.VipWinBonusPct, fallback.VipLossRebatePct, fallback.VipDailyCapChips, fallback.VipMaintainLpCost);
            if (string.IsNullOrWhiteSpace(json)) return fb;
            try
            {
                var rows = System.Text.Json.JsonSerializer.Deserialize<List<LevelRow>>(json, LadderJson);
                if (rows == null || rows.Count == 0) return fb;
                int n = rows.Count;
                var points = new long[n + 1]; var bonus = new decimal[n + 1]; var hold = new int[n + 1];
                var win = new decimal[n + 1]; var rebate = new decimal[n + 1]; var cap = new decimal[n + 1];
                var maintain = new long[n + 1];
                for (int i = 0; i < n; i++)
                {
                    var r = rows[i];
                    if (r == null || r.BonusPct < 0m || r.MaintainLp < 0L) return fb;
                    // Missing columns fall back to the built-in rung for the SAME level, never to zero (see LevelRow).
                    var req = r.PointsRequired ?? At(fallback.VipPointsRequired, i + 1);
                    var days = r.HoldDays ?? AtInt(fallback.VipHoldDays, i + 1);
                    var w = r.WinBonusPct ?? At(fallback.VipWinBonusPct, i + 1);
                    var lr = r.LossRebatePct ?? At(fallback.VipLossRebatePct, i + 1);
                    var dc = r.DailyCapChips ?? At(fallback.VipDailyCapChips, i + 1);
                    if (req <= 0L || days < 0 || w < 0m || lr < 0m || dc < 0m) return fb;
                    if (i > 0 && req <= points[i]) return fb;   // strictly increasing
                    points[i + 1] = req; bonus[i + 1] = r.BonusPct; hold[i + 1] = days;
                    win[i + 1] = w; rebate[i + 1] = lr; cap[i + 1] = dc; maintain[i + 1] = r.MaintainLp;
                }
                return (points, bonus, hold, win, rebate, cap, maintain);
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
