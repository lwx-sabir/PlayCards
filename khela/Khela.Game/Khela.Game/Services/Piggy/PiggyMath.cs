using System;

namespace Khela.Game.Services.Piggy
{
    /// <summary>
    /// Every piggy-bank decision, as pure functions — no database, no clock, no config lookup. The same split the
    /// pass and daily ladders use: the rules are the part worth testing exhaustively, and they are impossible to test
    /// exhaustively while they are tangled up with EF and Redis.
    /// </summary>
    public static class PiggyMath
    {
        /// <summary>
        /// What one settled round adds, before any capping.
        ///
        /// <paramref name="cleanWager"/> is the EARNED (non-gifted) stake — gifted chips filling a bank is the same
        /// two-account farm that gifted chips earning XP would be. <paramref name="netLoss"/> is what the round cost
        /// the player, zero or less meaning they broke even or won.
        /// </summary>
        public static decimal Accrual(decimal cleanWager, decimal netLoss, PiggyConfig cfg)
        {
            if (cfg == null) return 0m;

            decimal amount = 0m;

            if (cfg.Mode == PiggyMode.Wager || cfg.Mode == PiggyMode.Both)
                amount += Percent(Math.Max(0m, cleanWager), cfg.WagerRatePercent);

            if (cfg.Mode == PiggyMode.Loss || cfg.Mode == PiggyMode.Both)
                amount += Percent(Math.Max(0m, netLoss), cfg.LossRatePercent);

            return amount > 0m ? amount : 0m;
        }

        /// <summary>
        /// How much of <paramref name="wanted"/> actually fits, given the room left in the bank and the room left
        /// under today's cap. Returns 0 when either is exhausted.
        ///
        /// Both limits are applied here rather than at the call site because they interact: a bank that is nearly full
        /// AND nearly capped for the day takes the smaller of the two, and getting that wrong is how a bank ends up
        /// over its own maximum.
        /// </summary>
        public static decimal Fit(decimal wanted, decimal amount, decimal max, decimal accruedToday, PiggyConfig cfg)
        {
            if (wanted <= 0m) return 0m;

            var capacityLeft = max - amount;
            if (capacityLeft <= 0m) return 0m;              // full: accrual STOPS, it does not silently spill

            var allowed = Math.Min(wanted, capacityLeft);

            var dailyCap = DailyCap(max, cfg);
            if (dailyCap > 0m)
            {
                var dayLeft = dailyCap - Math.Max(0m, accruedToday);
                if (dayLeft <= 0m) return 0m;
                allowed = Math.Min(allowed, dayLeft);
            }

            return allowed > 0m ? allowed : 0m;
        }

        /// <summary>Today's ceiling in chips. 0 = uncapped.</summary>
        public static decimal DailyCap(decimal max, PiggyConfig cfg)
            => cfg == null || cfg.MaxAccrualPerDayPercent <= 0m ? 0m : Percent(max, cfg.MaxAccrualPerDayPercent);

        /// <summary>
        /// The tier a player of this level belongs in, 1-based, and its capacity.
        ///
        /// Walks the ladder rather than indexing it, so an unsorted or gappy tier table degrades to "the best rung
        /// they qualify for" instead of throwing on the settle path.
        /// </summary>
        public static (int Tier, decimal Max, string PriceSku) TierFor(int level, PiggyConfig cfg)
        {
            if (cfg?.Tiers == null || cfg.Tiers.Length == 0) return (1, 0m, "");

            // The highest rung the player has reached.
            int best = -1;
            for (int i = 0; i < cfg.Tiers.Length; i++)
            {
                var t = cfg.Tiers[i];
                if (t == null || level < t.MinLevel) continue;
                if (best < 0 || t.MinLevel > cfg.Tiers[best].MinLevel) best = i;
            }

            // Below every rung — a ladder misconfigured to start above level 1. Fall back to the lowest rung rather
            // than handing the player a bank with no capacity, which would read as the feature being broken.
            if (best < 0)
            {
                best = 0;
                for (int i = 1; i < cfg.Tiers.Length; i++)
                    if (cfg.Tiers[i] != null && cfg.Tiers[i].MinLevel < cfg.Tiers[best].MinLevel) best = i;
            }

            var rung = cfg.Tiers[best] ?? new PiggyTier();
            return (best + 1, rung.MaxAmount, rung.PriceSku ?? "");
        }

        /// <summary>
        /// Has this bank's window run out by <paramref name="nowUtc"/>?
        ///
        /// A null <paramref name="expiresAtUtc"/> is the normal state, not an edge case: the clock only exists once
        /// the player has been SHOWN a full bank. A bank that is still filling, or one that filled while they were
        /// away and they haven't opened the game since, has no deadline at all.
        /// </summary>
        public static bool IsExpired(DateTime nowUtc, DateTime? expiresAtUtc)
            => expiresAtUtc != null && nowUtc >= expiresAtUtc.Value;

        /// <summary>When a window starting now would run out. Null when expiry is switched off.</summary>
        public static DateTime? WindowEnd(DateTime startUtc, PiggyConfig cfg)
            => cfg == null || cfg.CycleHours <= 0 ? (DateTime?)null : startUtc.AddHours(cfg.CycleHours);

        /// <summary>Seconds left on the clock, floored at zero. 0 with no window means "no countdown to show".</summary>
        public static long SecondsLeft(DateTime nowUtc, DateTime? expiresAtUtc)
        {
            if (expiresAtUtc == null) return 0;
            var left = (long)(expiresAtUtc.Value - nowUtc).TotalSeconds;
            return left > 0 ? left : 0;
        }

        /// <summary>Is this bank buyable? Full enough by the configured threshold, and holding something.</summary>
        public static bool CanBreak(decimal amount, decimal max, PiggyConfig cfg)
        {
            if (cfg == null || max <= 0m || amount <= 0m) return false;
            var need = Percent(max, cfg.MinBreakPercent <= 0m ? 100m : cfg.MinBreakPercent);
            return amount >= need;
        }

        /// <summary>
        /// Capacity of the rung a store product was SOLD for (its <c>params.tier</c>, 1-based, the same ordinal
        /// <see cref="TierFor"/> hands out), or null when the product carries no tier or the rung no longer exists.
        /// </summary>
        public static decimal? SoldCapacity(int soldTier, PiggyConfig cfg)
        {
            if (soldTier < 1 || cfg?.Tiers == null || soldTier > cfg.Tiers.Length) return null;
            var rung = cfg.Tiers[soldTier - 1];
            return rung == null || rung.MaxAmount <= 0m ? (decimal?)null : rung.MaxAmount;
        }

        /// <summary>
        /// What a VERIFIED break pays before the option multiplier.
        ///
        /// <paramref name="bankRule"/> is what the bank itself says (what it holds; its capacity when the offer was
        /// charged and the bank moved). That is the whole answer when the product was sold for the bank's own rung —
        /// including a bank whose capacity was snapshotted before the ladder was edited, which must still pay what
        /// the player filled.
        ///
        /// When the product was sold for a DIFFERENT rung the payout is capped at that rung's capacity. The store
        /// only verifies that a product was paid for, not which bank it is applied to, and a break pays out of the
        /// player's own bank — so without this cap the cheapest rung's product bought a level-25 bank at a level-1
        /// price, repeatably, and beat every chip pack. The cap pays exactly what the sold rung's price bought, so
        /// the honest case it also covers — a level-up between the tap and the receipt — is never under-delivered.
        /// A sold rung that no longer exists (ladder shortened) is not capped: every such product was priced above
        /// the ladder that remains.
        /// </summary>
        public static decimal PayoutBase(decimal bankRule, int bankTier, int soldTier, PiggyConfig cfg, out bool capped)
        {
            capped = false;
            if (soldTier <= 0 || soldTier == bankTier) return bankRule;
            var sold = SoldCapacity(soldTier, cfg);
            if (!sold.HasValue || sold.Value >= bankRule) return bankRule;
            capped = true;
            return sold.Value;
        }

        /// <summary>0..1 for the bar. Clamped, so a bank that somehow exceeded its max still draws as full.</summary>
        public static float Percent01(decimal amount, decimal max)
        {
            if (max <= 0m) return 0f;
            var p = (float)(amount / max);
            return p < 0f ? 0f : (p > 1f ? 1f : p);
        }

        private static decimal Percent(decimal value, decimal percent)
            => percent <= 0m ? 0m : Math.Round(value * percent / 100m, 4, MidpointRounding.ToZero);
    }
}
