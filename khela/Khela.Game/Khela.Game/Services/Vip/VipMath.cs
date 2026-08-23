using System;
using Khela.Common.Leaderboards;

namespace Khela.Game.Services.Vip
{
    /// <summary>
    /// Pure VIP arithmetic (docs/VIP_SPEC.md §2 + §4) — no DB/clock, fully unit-testable. SP is FLAT ×1 and never from
    /// winnings; the TIER is the band of the season's SP. The VIP LEVEL is a different ladder entirely: the band of the
    /// VIP-P bought inside the trailing window, held for a while by the purchase that reached it. The COMP multiplier
    /// = <c>1 + TierBonus + VipLevelBonus</c> (additive) and rides the Loyalty/store/faucet tracks — it never touches
    /// odds, a paytable or a settle. (The win bonus / loss rebate the level also carries is a post-settle promotion
    /// paid from the closed day's net; see §4.)
    /// </summary>
    public static class VipMath
    {
        // ---- SP accrual (flat ×1) ----
        public static long SpFromWager(decimal cleanWager, VipConfig c)
            => (cleanWager <= 0m || c.SpChipsPerPoint <= 0m) ? 0L : (long)Math.Floor(cleanWager / c.SpChipsPerPoint);

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

        // ---- COMP multiplier (tier + VIP level, additive) — applies to comp/faucets, NEVER to odds ----
        public static decimal ComboMultiplier(VipTier tier, int vipLevel, VipConfig c)
            => 1m + TierBonus(tier, c) + VipLevelBonus(vipLevel, c);

        public static decimal TierBonus(VipTier tier, VipConfig c) => Get(c.TierBonusPct, (int)tier, 0m);
        public static decimal VipLevelBonus(int vipLevel, VipConfig c) => Get(c.VipLevelBonusPct, vipLevel, 0m);

        // ---- VIP Level (the money ladder, §4) ----

        /// <summary>The top VIP level THIS ladder defines (the arrays are admin-editable, so it is not always 10).</summary>
        public static int TopVipLevel(VipConfig c) => VipConfig.TopLevel(c);

        /// <summary>VIP-P inside the window needed to STAND at <paramref name="level"/>; <c>long.MaxValue</c> past the top
        /// (so "how far to the next level" past the ladder's end is never asked as a real number).</summary>
        public static long PointsRequired(int level, VipConfig c)
            => (c?.VipPointsRequired != null && level >= 0 && level < c.VipPointsRequired.Length) ? c.VipPointsRequired[level] : long.MaxValue;

        /// <summary>
        /// The VIP level a window of VIP-P stands at — the highest rung whose bar it clears, 0 if it clears none.
        /// This is the whole decay mechanism: the window drains as purchases age out, and the band falls with it.
        /// </summary>
        public static int LevelFromPoints(long windowPoints, VipConfig c)
        {
            if (windowPoints <= 0L) return 0;
            for (int lvl = TopVipLevel(c); lvl >= 1; lvl--)
            {
                var bar = PointsRequired(lvl, c);
                if (bar > 0L && bar != long.MaxValue && windowPoints >= bar) return lvl;
            }
            return 0;
        }

        /// <summary>Days a purchase that reached <paramref name="level"/> holds it for; 0 = no hold (the window alone).</summary>
        public static int HoldDays(int level, VipConfig c)
            => (c?.VipHoldDays != null && level >= 0 && level < c.VipHoldDays.Length) ? Math.Max(0, c.VipHoldDays[level]) : 0;

        /// <summary>Fraction of a winning day's net credited back at this level (§4). Dormant until the rebate ships.</summary>
        public static decimal WinBonusPct(int level, VipConfig c) => Get(c?.VipWinBonusPct, level, 0m);

        /// <summary>Fraction of a losing day's net credited back at this level (§4). Dormant until the rebate ships.</summary>
        public static decimal LossRebatePct(int level, VipConfig c) => Get(c?.VipLossRebatePct, level, 0m);

        /// <summary>Chips-per-day ceiling on the win bonus / rebate at this level. 0 = the feature is OFF for that level.</summary>
        public static decimal DailyCapChips(int level, VipConfig c) => Get(c?.VipDailyCapChips, level, 0m);

        /// <summary>LP cost to extend the hold on the given VIP level; 0 if out of range.</summary>
        public static long MaintainLpCost(int level, VipConfig c)
            => (c.VipMaintainLpCost != null && level >= 0 && level < c.VipMaintainLpCost.Length) ? c.VipMaintainLpCost[level] : 0L;

        /// <summary>
        /// The level a player actually stands at: the window band, or a still-valid HELD level above it (§4). The hold is
        /// what a purchase buys beyond the window — it never LOWERS the band, so a player who keeps buying is never held
        /// down by an old snapshot.
        /// </summary>
        public static int EffectiveLevel(long windowPoints, int heldLevel, DateTime? heldThrough, DateTime now, VipConfig c)
        {
            var band = LevelFromPoints(windowPoints, c);
            if (heldLevel > band && heldThrough.HasValue && now < heldThrough.Value)
                return Math.Min(heldLevel, TopVipLevel(c));
            return band;
        }

        /// <summary>
        /// Whether a VIP-P credit that leaves the window standing at <paramref name="band"/> should RE-ARM the hold
        /// (docs/VIP_SPEC.md §4): yes if it reaches or keeps the band the player is already holding, or if the old hold
        /// has lapsed and there is nothing left to protect — no if it lands BELOW a live hold. That last case is the
        /// whole rule: without it, a $1.99 pack with ten VIP-P would renew VIP 10 forever.
        /// </summary>
        public static bool ShouldRearmHold(int band, int heldLevel, DateTime? heldThrough, DateTime now)
        {
            bool holdLive = heldLevel > 0 && heldThrough.HasValue && now < heldThrough.Value;
            return band >= heldLevel || !holdLive;
        }

        // ---- what a level is expected to COST (docs/VIP_SPEC.md §5) ----

        /// <summary>Hands a day of play is modelled as, for the expected-return estimate below.</summary>
        public const int ModelHandsPerDay = 200;
        /// <summary>Standard deviation of ONE blackjack hand, in bet units (the textbook ≈1.15 for basic strategy).</summary>
        public const double ModelSdPerHand = 1.15;
        /// <summary>The house edge the estimate is judged against — blackjack basic strategy, ≈0.5%. Other games differ.</summary>
        public const decimal ModelHouseEdge = 0.005m;

        /// <summary>
        /// What a level's win bonus + loss rebate is expected to hand back, as a FRACTION OF HANDLE — the number that
        /// says whether a row is a comp or a +EV faucet. Compare it against <see cref="ModelHouseEdge"/>: above the edge
        /// and the level pays the player to play.
        ///
        /// A day's net is modelled as Normal(−edge × handle, σ), σ = 1.15 ÷ √hands per unit of handle, from which
        /// E[max(0, net)] = σ·φ(m) + μ·Φ(m) and E[max(0, −net)] = that − μ. It IGNORES the daily cap and the
        /// %-of-handle ceiling, both of which only ever reduce the payout.
        ///
        /// It is a MODEL, and its one soft spot is session length: a SHORTER day swings harder relative to its handle,
        /// so it costs MORE, not less. The shipped ladder is therefore set well under the edge rather than just under
        /// it, so the number survives a player who plays fifty hands instead of two hundred. Only telemetry settles it.
        /// </summary>
        public static decimal ExpectedReturnOfHandle(int level, VipConfig c, decimal houseEdge)
        {
            var win = (double)WinBonusPct(level, c);
            var reb = (double)LossRebatePct(level, c);
            if (win <= 0d && reb <= 0d) return 0m;
            if (DailyCapChips(level, c) <= 0m) return 0m;   // 0 = the feature is OFF at this level, so it pays nothing

            double sigma = ModelSdPerHand / Math.Sqrt(Math.Max(1, ModelHandsPerDay));
            double mu = -(double)houseEdge;
            double m = mu / sigma;
            double eUp = sigma * NormalPdf(m) + mu * NormalCdf(m);   // expected winning-day net
            double eDown = eUp - mu;                                  // expected losing-day net (E[max(0,-X)])
            var result = win * eUp + reb * eDown;
            return result <= 0d ? 0m : (decimal)result;
        }

        private static double NormalPdf(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);
        private static double NormalCdf(double x) => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));

        /// <summary>Abramowitz &amp; Stegun 7.1.26 — |error| &lt; 1.5e-7, far tighter than the model's own assumptions.</summary>
        private static double Erf(double x)
        {
            int sign = x < 0 ? -1 : 1;
            x = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.3275911 * x);
            double y = 1.0 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
            return sign * y;
        }

        // ---- badge / bars ----
        public static bool HasBadge(VipTier tier) => (int)tier >= (int)VipTier.Silver;

        public static long SpBar(VipConfig c, int tierInt)
            => (c.SpThresholds != null && tierInt >= 0 && tierInt < c.SpThresholds.Length) ? c.SpThresholds[tierInt] : long.MaxValue;

        public static decimal SpendFloor(VipConfig c, int tierInt)
            => (c.SpendFloorsUsd != null && tierInt >= 0 && tierInt < c.SpendFloorsUsd.Length) ? c.SpendFloorsUsd[tierInt] : decimal.MaxValue;

        /// <summary>The tier a given tier falls to at the season roll (docs/VIP_SPEC.md §2); a missing rung stays put.</summary>
        public static int ResetTo(VipConfig c, int tierInt)
            => (c?.TierResetTo != null && tierInt >= 0 && tierInt < c.TierResetTo.Length) ? c.TierResetTo[tierInt] : tierInt;

        private static decimal Get(decimal[] a, int i, decimal d) => (a != null && i >= 0 && i < a.Length) ? a[i] : d;
    }
}
