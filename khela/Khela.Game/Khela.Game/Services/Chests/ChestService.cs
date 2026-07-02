using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Chests;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Chests
{
    public interface IChestService
    {
        /// <summary>Open a chest (identified by <paramref name="chestKey"/> + <paramref name="tier"/>): for each permitted
        /// currency in its def, roll a uniform random amount in [min, max] (DETERMINISTIC per <paramref name="idemKey"/>,
        /// so a retry rolls the SAME amounts) and credit it idempotently. Returns the rolled rewards for the open animation.</summary>
        Task<ChestOpenResultDto> OpenAsync(Guid userId, string chestKey, ChestTier tier, string idemKey);

        /// <summary>All chests in the effective catalog — identity + display text only (for pickers/UI). No reward ranges.</summary>
        Task<List<ChestInfoDto>> ListAsync();
    }

    /// <summary>
    /// Opens chests money-safely. Amounts are a uniform random in each currency's [min, max], seeded by the chest's
    /// <c>idemKey</c> so they're the SAME on every retry — combined with the idempotent <see cref="IWalletService"/>
    /// credit (keyed on the same open), a double-tap can never double-pay AND the reported amount (the wallet's actual
    /// applied delta) always equals the credited amount. Which currencies are eligible is the allowlist in
    /// <see cref="ChestCatalog.RollRewards"/> — a token / undefined / non-permitted currency is dropped there, so a bad
    /// override (even one written straight to Redis, bypassing the admin-save validation) can never be credited.
    /// </summary>
    public sealed class ChestService : IChestService
    {
        private readonly IWalletService _wallet;
        private readonly IRedisService _redis;
        private readonly ILogger<ChestService> _logger;

        public ChestService(IWalletService wallet, IRedisService redis, ILogger<ChestService> logger)
        {
            _wallet = wallet; _redis = redis; _logger = logger;
        }

        public async Task<ChestOpenResultDto> OpenAsync(Guid userId, string chestKey, ChestTier tier, string idemKey)
        {
            if (string.IsNullOrEmpty(idemKey)) return Fail("Missing open key.");
            if (string.IsNullOrEmpty(chestKey)) return Fail("Missing chest key.");
            var cfg = await EffectiveAsync();
            var def = cfg.Find(chestKey, tier);
            if (def == null || def.Rewards == null || def.Rewards.Count == 0) return Fail($"Unknown chest '{chestKey} / {tier}'.");

            // Surface any non-permitted currency a bad override slipped past admin-save validation (it's dropped by RollRewards).
            foreach (var r in def.Rewards)
                if (!ChestCatalog.IsAllowedReward(r.Currency))
                    _logger.LogWarning("Chest {Key}/{Tier} has a non-permitted reward currency {Currency} — skipped.", chestKey, tier, r.Currency);

            var rewards = new List<RolledRewardDto>();
            foreach (var (currency, amount) in ChestCatalog.RollRewards(def, idemKey))
            {
                // Report the amount the WALLET actually applied (its return value), not the locally-rolled value — so a
                // config edit between a partial open and its retry can't make the shown reward disagree with the credited one.
                var txn = await _wallet.CreditAsync(userId.ToString(), currency, amount, TransactionType.Bonus,
                    $"chest:{idemKey}:{(int)currency}", new WalletContext { Description = $"{def.Title ?? def.Key} ({tier})" });
                rewards.Add(new RolledRewardDto { Currency = (int)currency, Amount = txn?.Amount ?? amount });
            }

            return new ChestOpenResultDto
            {
                Ok = true,
                ChestType = def.Key,
                Tier = tier.ToString(),
                Title = def.Title,
                Description = def.Description,
                IconKey = def.IconKey,
                Rewards = rewards,
                NewChipBalance = await _wallet.GetBalanceAsync(userId.ToString(), CurrencyType.Chips),
            };
        }

        public async Task<List<ChestInfoDto>> ListAsync()
        {
            var cfg = await EffectiveAsync();
            return cfg.Chests
                .Select(c => new ChestInfoDto { Key = c.Key, Tier = c.Tier.ToString(), Title = c.Title, Description = c.Description })
                .ToList();
        }

        private async Task<ChestConfig> EffectiveAsync()
        {
            try
            {
                var json = await _redis.GetDatabase().StringGetAsync(ChestCatalog.RedisKey);
                if (json.HasValue)
                {
                    var cfg = ChestCatalog.TryParse(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { /* Redis down / bad override → defaults */ }
            return ChestCatalog.Defaults();
        }

        private static ChestOpenResultDto Fail(string error) => new ChestOpenResultDto { Ok = false, Error = error };
    }
}
