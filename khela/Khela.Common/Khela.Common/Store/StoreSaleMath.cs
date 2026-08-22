using System;
using System.Collections.Generic;
using Khela.Common.Rewards;

namespace Khela.Common.Store
{
    /// <summary>
    /// The ONE formula for a value-bonus sale, shared by the server (which grants) and the client (which shows). It lives
    /// here so the number on the card and the number in the ledger cannot drift: if either side computed its own, a
    /// rounding difference would be a support ticket about money.
    /// </summary>
    public static class StoreSaleMath
    {
        /// <summary>Amount paid during a +<paramref name="percent"/>% sale — floored to a whole unit, never rounded up.</summary>
        public static decimal Boost(decimal amount, int percent)
        {
            if (amount <= 0m || percent <= 0) return amount;
            return Math.Floor(amount * (100m + percent) / 100m);
        }

        /// <summary>
        /// The product's lines as a sale pays them: currency and XP lines boosted, every other kind (chest, cosmetic,
        /// item) untouched — "+50%" of a cosmetic has no meaning. Returns a NEW list; the input is not modified.
        /// </summary>
        public static List<RewardGrant> Apply(IEnumerable<RewardGrant> lines, int percent)
        {
            var result = new List<RewardGrant>();
            if (lines == null) return result;
            foreach (var line in lines)
            {
                if (line == null) continue;
                bool boostable = percent > 0 && (line.Kind == RewardKind.Currency || line.Kind == RewardKind.Xp);
                result.Add(new RewardGrant
                {
                    Kind = line.Kind,
                    Id = line.Id,
                    Amount = boostable ? Boost(line.Amount, percent) : line.Amount,
                    Images = line.Images,
                });
            }
            return result;
        }
    }
}
