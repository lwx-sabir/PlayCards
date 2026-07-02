using System;

namespace Khela.Game.Services.Loyalty
{
    /// <summary>Pure Loyalty arithmetic (Progression Spec §4) — LP earned from clean wager (or purchase) × the VIP
    /// benefit multiplier. No DB/clock, so it is fully unit-testable.</summary>
    public static class LoyaltyMath
    {
        /// <summary>LP from one round's EARNED (clean) wager, scaled by the player's VIP benefit multiplier.</summary>
        public static long LpFromWager(decimal cleanWager, decimal vipMultiplier, LoyaltyConfig c)
        {
            if (cleanWager <= 0m || c.LpChipsPerPoint <= 0m) return 0L;
            var basis = Math.Floor(cleanWager / c.LpChipsPerPoint);
            var mult = vipMultiplier <= 0m ? 1m : vipMultiplier;
            return (long)Math.Floor(basis * mult);
        }

        /// <summary>LP drip from a verified purchase × the VIP multiplier (dormant until LpPerUsd &gt; 0 + IAP exists).</summary>
        public static long LpFromPurchase(decimal usdSpent, decimal vipMultiplier, LoyaltyConfig c)
        {
            if (usdSpent <= 0m || c.LpPerUsd <= 0m) return 0L;
            var basis = Math.Floor(usdSpent * c.LpPerUsd);
            var mult = vipMultiplier <= 0m ? 1m : vipMultiplier;
            return (long)Math.Floor(basis * mult);
        }
    }
}
