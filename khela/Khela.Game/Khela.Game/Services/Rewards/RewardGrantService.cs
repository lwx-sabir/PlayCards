using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Rewards
{
    /// <summary>
    /// Hands a reward payload to the right <see cref="IRewardGranter"/> per line. This is the ONE place a granting
    /// system (pass, missions, chests, level-ups) has to talk to in order to pay out anything the game can give —
    /// chips, Kash, XP today; tickets, clothes, other currencies later — without knowing how any of it works.
    /// </summary>
    public interface IRewardGrantService
    {
        /// <summary>
        /// Grant a whole payload. Each line gets its own idempotency key <c>{idemKey}:{index}</c> — a fixed function of
        /// the line's POSITION, so a retry lands on exactly the same keys and cannot double-pay. Returns the lines that
        /// were actually applied (a chest expands into its rolled contents).
        ///
        /// An invalid or unsupported line is SKIPPED and logged; a granter that FAILS (wallet error, DB error) throws
        /// through, deliberately: the caller must not record "granted" for a payload that didn't fully pay out — it
        /// should leave its claim row incomplete and let the idempotent retry finish the job.
        /// </summary>
        Task<List<GrantedLineDto>> GrantAllAsync(Guid userId, IReadOnlyList<RewardGrant> lines, string idemKey,
            string description = null, string externalRef = null);

        /// <summary>Grant a SINGLE line under the key verbatim (no <c>:index</c> suffix) — for callers whose unit of
        /// idempotency is already one line, e.g. a claimed <c>PlayerRewards</c> inbox row.</summary>
        Task<List<GrantedLineDto>> GrantOneAsync(Guid userId, RewardGrant line, string idemKey,
            string description = null, string externalRef = null);

        /// <summary>True if a granter is registered for this kind — used by admin-save validation so a reward that
        /// can't be paid out yet is rejected at authoring time rather than skipped at claim time.</summary>
        bool CanGrant(RewardKind kind);
    }

    /// <inheritdoc cref="IRewardGrantService"/>
    public sealed class RewardGrantService : IRewardGrantService
    {
        private readonly Dictionary<RewardKind, IRewardGranter> _granters = new Dictionary<RewardKind, IRewardGranter>();
        private readonly ILogger<RewardGrantService> _logger;

        public RewardGrantService(IEnumerable<IRewardGranter> granters, ILogger<RewardGrantService> logger)
        {
            _logger = logger;
            foreach (var g in granters ?? Array.Empty<IRewardGranter>())
                _granters[g.Kind] = g;   // last registration wins (lets a deployment override one kind)
        }

        public bool CanGrant(RewardKind kind) => _granters.ContainsKey(kind);

        public async Task<List<GrantedLineDto>> GrantAllAsync(Guid userId, IReadOnlyList<RewardGrant> lines, string idemKey,
            string description = null, string externalRef = null)
        {
            var applied = new List<GrantedLineDto>();
            if (lines == null || lines.Count == 0 || string.IsNullOrEmpty(idemKey)) return applied;

            for (int i = 0; i < lines.Count; i++)
                applied.AddRange(await GrantOneAsync(userId, lines[i], $"{idemKey}:{i}", description, externalRef ?? idemKey));

            return applied;
        }

        public async Task<List<GrantedLineDto>> GrantOneAsync(Guid userId, RewardGrant line, string idemKey,
            string description = null, string externalRef = null)
        {
            var applied = new List<GrantedLineDto>();
            if (line == null || string.IsNullOrEmpty(idemKey)) return applied;

            if (!_granters.TryGetValue(line.Kind, out var granter))
            {
                // Not payable on this build. Admin-save validation (CanGrant) is the real gate; skipping here keeps a
                // prematurely-configured reward from blocking every claim.
                _logger.LogError("Reward line skipped: no granter registered for kind {Kind} (id '{Id}').", line.Kind, line.Id);
                return applied;
            }

            var result = await granter.GrantAsync(userId, line, idemKey, description, externalRef);
            if (result != null) applied.AddRange(result);
            return applied;
        }
    }
}
