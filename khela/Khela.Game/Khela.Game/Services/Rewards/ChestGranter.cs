using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Khela.Game.Services.Chests;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Rewards
{
    /// <summary>
    /// Grants a chest (<c>Id = "CK_Chest:Rare"</c>, <c>Amount</c> = how many). Each open goes through
    /// <see cref="IChestService"/>, which rolls DETERMINISTICALLY from the open key and credits idempotently — so a
    /// retry re-rolls the same amounts and cannot double-pay. Multiple chests get one key each (<c>…:#n</c>).
    ///
    /// Expands in the result: the chest line itself plus the currency lines it actually rolled, so the client can
    /// animate the contents instead of an opaque box.
    /// </summary>
    public sealed class ChestGranter : IRewardGranter
    {
        private const int MaxPerLine = 20;   // sanity bound on a single line's count (a config typo shouldn't open 10k chests)

        private readonly IChestService _chests;
        private readonly ILogger<ChestGranter> _logger;

        public ChestGranter(IChestService chests, ILogger<ChestGranter> logger)
        {
            _chests = chests; _logger = logger;
        }

        public RewardKind Kind => RewardKind.Chest;

        public async Task<IReadOnlyList<GrantedLineDto>> GrantAsync(Guid userId, RewardGrant line, string idemKey, string description, string externalRef = null)
        {
            if (line == null) return null;
            if (!RewardIds.TryParseChest(line.Id, out var chestKey, out var tier))
            {
                _logger.LogWarning("Chest reward skipped: '{Id}' is not a \"key:tier\" chest id.", line.Id);
                return null;
            }

            var count = (int)decimal.Floor(line.Amount <= 0m ? 1m : line.Amount);
            if (count > MaxPerLine)
            {
                _logger.LogWarning("Chest reward '{Id}' asked for {Count} — clamped to {Max}.", line.Id, count, MaxPerLine);
                count = MaxPerLine;
            }

            var applied = new List<GrantedLineDto>();
            int opened = 0;
            for (int i = 0; i < count; i++)
            {
                var result = await _chests.OpenAsync(userId, chestKey, tier, count == 1 ? idemKey : $"{idemKey}#{i}");
                if (result == null || !result.Ok)
                {
                    // A misconfigured chest must not silently swallow the rest of the payload.
                    _logger.LogError("Chest reward '{Id}' failed to open: {Error}", line.Id, result?.Error);
                    break;
                }
                opened++;
                if (result.Rewards == null) continue;
                foreach (var rolled in result.Rewards)
                    applied.Add(new GrantedLineDto
                    {
                        Kind = (int)RewardKind.Currency,
                        Id = ((Khela.Game.Database.Models.CurrencyType)rolled.Currency).ToString(),
                        Amount = rolled.Amount,
                    });
            }
            if (opened == 0) return null;

            applied.Insert(0, new GrantedLineDto { Kind = (int)RewardKind.Chest, Id = line.Id, Amount = opened });
            return applied;
        }
    }
}
