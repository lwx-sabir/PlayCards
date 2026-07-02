using System;
using Khela.Common.Leaderboards;

namespace Khela.Game.Services.Vip
{
    /// <summary>
    /// Pure VIP arithmetic (Progression Spec §3 + §3.6) — no DB/clock, fully unit-testable. SP is FLAT ×1 and never
    /// from winnings. The COMP multiplier = <c>1 + TierBonus + VipLevelBonus</c> (additive) and applies to the
    /// Loyalty/store/faucet tracks ONLY — NEVER to winnings. VIP Level is ground from <c>SP × tierFactor</c>.
    /// </summary>
    public static class VipMath
    {
        // ---- SP accrual (flat ×1) ----
        public static long SpFromWager(decimal cleanWager, VipConfig c)
            => (cleanWager <= 0m || c.SpChipsPerPoint <= 0m) ? 0L : (long)Math.Floor(cleanWager / c.SpChipsPerPoint);

        public static long SpFromPurchase(decimal usdSpent, VipConfig c)
            => usdSpent <= 0m ? 0L : (long)Math.Floor(usdSpent * c.SpPerUsd);

        // ---- Tier (rank) resolution ----
        /// <summary>The tier from trailing SP + spend + level. None below the entry level; Bronze (floor) at/above it;
        /// Silver+ require BOTH the SP bar and the USD spend floor. Highest band met (caller does max(current, this)).</summary>
        public static VipTier ResolveBand(long trailingSp, decimal trailingSpendUsd, int level, VipConfig c)
        {
            if (level < c.VipEntryLevel) return VipTier.None;
            for (int t = (int)VipTier.BlackDiamond; t >= (int)VipTier.Silver; t--)
                if (trailingSp >= SpBar(c, t) && trailingSpendUsd >= SpendFloor(c, t))
                    return (VipTier)t;
            return VipTier.Bronze;
        }

        // ---- COMP multiplier (tier + VIP level, additive) — applies to comp/faucets, NEVER to winnings ----
        public static decimal ComboMultiplier(VipTier tier, int vipLevel, VipConfig c)
            => 1m + TierBonus(tier, c) + VipLevelBonus(vipLevel, c);

        public static decimal TierBonus(VipTier tier, VipConfig c) => Get(c.TierBonusPct, (int)tier, 0m);
        public static decimal VipLevelBonus(int vipLevel, VipConfig c) => Get(c.VipLevelBonusPct, vipLevel, 0m);

        // ---- VIP Level grind ----
        public static decimal TierFactor(VipTier tier, VipConfig c) => Get(c.TierFactors, (int)tier, 0m);

        /// <summary>VIP-Level grind progress from a round's SP, accelerated by the player's tier (None earns none).</summary>
        public static long VipProgressFromSp(long sp, VipTier tier, VipConfig c)
            => sp <= 0L ? 0L : (long)Math.Floor(sp * TierFactor(tier, c));

        public static long VipLevelThreshold(int level, VipConfig c)
            => (c.VipLevelThresholds != null && level >= 0 && level < c.VipLevelThresholds.Length) ? c.VipLevelThresholds[level] : long.MaxValue;

        /// <summary>LP cost to maintain (extend a period on) the given VIP level; 0 if out of range.</summary>
        public static long MaintainLpCost(int level, VipConfig c)
            => (c.VipMaintainLpCost != null && level >= 0 && level < c.VipMaintainLpCost.Length) ? c.VipMaintainLpCost[level] : 0L;

        /// <summary>Fold gained grind progress into the into-level counter, leveling up with carry-over (cap 10).
        /// Returns the new into-level progress + the new VIP level.</summary>
        public static (long Progress, int Level) ApplyVipLevelUps(long progress, int level, long gained, VipConfig c)
        {
            if (level < 0) level = 0;
            progress += gained;
            while (level < 10)
            {
                long need = VipLevelThreshold(level + 1, c);
                if (need <= 0L || progress < need) break;
                progress -= need;
                level++;
            }
            return (progress, level);
        }

        // ---- badge / bars ----
        public static bool HasBadge(VipTier tier) => (int)tier >= (int)VipTier.Silver;

        public static long SpBar(VipConfig c, int tierInt)
            => (c.SpThresholds != null && tierInt >= 0 && tierInt < c.SpThresholds.Length) ? c.SpThresholds[tierInt] : long.MaxValue;

        public static decimal SpendFloor(VipConfig c, int tierInt)
            => (c.SpendFloorsUsd != null && tierInt >= 0 && tierInt < c.SpendFloorsUsd.Length) ? c.SpendFloorsUsd[tierInt] : decimal.MaxValue;

        private static decimal Get(decimal[] a, int i, decimal d) => (a != null && i >= 0 && i < a.Length) ? a[i] : d;
    }
}
