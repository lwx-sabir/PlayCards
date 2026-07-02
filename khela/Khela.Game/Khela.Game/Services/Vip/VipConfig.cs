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
        public decimal[] SpendFloorsUsd { get; init; } = new decimal[] { 0m, 0m, 0m, 0m, 30m, 150m, 800m, 4_000m };
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
                SpThresholds = b.SpThresholds, SpendFloorsUsd = b.SpendFloorsUsd, TierBonusPct = b.TierBonusPct,
                TierFactors = b.TierFactors, VipLevelBonusPct = b.VipLevelBonusPct,
                VipLevelThresholds = b.VipLevelThresholds, VipMaintainLpCost = b.VipMaintainLpCost,
            };
        }

        private static decimal Dec(IReadOnlyDictionary<string, string> o, string k, decimal d)
            => o.TryGetValue(k, out var v) && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : d;
        private static long Lng(IReadOnlyDictionary<string, string> o, string k, long d)
            => o.TryGetValue(k, out var v) && long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : d;
        private static int Int(IReadOnlyDictionary<string, string> o, string k, int d)
            => o.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : d;
    }
}
