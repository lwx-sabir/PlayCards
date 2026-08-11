using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Khela.Game.Services.Progression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Rewards
{
    /// <summary>
    /// Grants progression XP through the one XP core (<see cref="IProgressionService.GrantXpAsync"/>), so a reward's
    /// XP auto-levels the player and fires level rewards exactly like round accrual does.
    ///
    /// Reward XP is granted with <c>bypassDailyCap: true</c> — a decision, not an oversight: reward XP is
    /// server-authored config on a claim-gated (and for the pass, paid) track, so it isn't farmable, and clipping a
    /// purchased reward against a cap the player can't see is a support ticket waiting to happen. Round accrual and
    /// every other flat grant stay capped.
    ///
    /// <see cref="IProgressionService"/> is resolved LAZILY (not constructor-injected): ProgressionService depends on
    /// IRewardService, which depends on IRewardGrantService, which owns this granter — constructor injection would be
    /// a DI cycle. By the time a grant actually runs, the scope already holds the constructed instances.
    /// </summary>
    public sealed class XpGranter : IRewardGranter
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<XpGranter> _logger;

        public XpGranter(IServiceProvider sp, ILogger<XpGranter> logger)
        {
            _sp = sp; _logger = logger;
        }

        public RewardKind Kind => RewardKind.Xp;

        public async Task<IReadOnlyList<GrantedLineDto>> GrantAsync(Guid userId, RewardGrant line, string idemKey, string description, string externalRef = null)
        {
            if (line == null || line.Amount <= 0m) return null;
            var amount = (long)decimal.Floor(line.Amount);
            if (amount <= 0) return null;

            var progression = _sp.GetService<IProgressionService>();
            if (progression == null) { _logger.LogError("XP reward skipped: no IProgressionService registered."); return null; }

            // Idempotent inside GrantXpAsync on (source, idemKey). Returns the XP actually applied — 0 for a replay
            // or when the progression layer is disabled, in which case nothing is reported as granted.
            var granted = await progression.GrantXpAsync(userId, amount, "reward", idemKey, bypassDailyCap: true);
            if (granted <= 0) return null;

            return new[] { new GrantedLineDto { Kind = (int)RewardKind.Xp, Amount = granted } };
        }
    }
}
