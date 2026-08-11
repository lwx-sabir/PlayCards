using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Rewards
{
    public interface IRewardService
    {
        /// <summary>ENQUEUE a claimable CURRENCY reward (Pending) — does NOT credit the wallet. Idempotent on
        /// <paramref name="idemKey"/> (a given grant enqueues exactly once). Call from any granting system
        /// (level-up, daily bonus, pass, achievement…).</summary>
        Task GrantAsync(Guid userId, RewardSource source, CurrencyType currency, decimal amount, string title,
            string idemKey, string metadataJson = null, DateTime? expiresAt = null);

        /// <summary>ENQUEUE a claimable reward of ANY kind — currency, XP, a chest, and (as their granters ship)
        /// cosmetics or items. Same idempotency and claim-to-collect rules as <see cref="GrantAsync"/>.</summary>
        Task GrantLineAsync(Guid userId, RewardSource source, RewardGrant line, string title,
            string idemKey, string metadataJson = null, DateTime? expiresAt = null);

        /// <summary>The caller's pending (unclaimed, unexpired) rewards — the inbox.</summary>
        Task<List<RewardDto>> GetPendingAsync(Guid userId);

        /// <summary>Claim ONE reward: pay it out then mark it claimed. Idempotent on the reward id, so a
        /// double-tap never double-pays.</summary>
        Task<ClaimResultDto> ClaimAsync(Guid userId, Guid rewardId);

        /// <summary>Claim ALL pending rewards for the caller.</summary>
        Task<ClaimResultDto> ClaimAllAsync(Guid userId);
    }

    /// <summary>
    /// The claimable-reward inbox. Granting systems ENQUEUE rewards here (Pending); payout happens only when the
    /// player CLAIMS, through <see cref="IRewardGrantService"/> keyed on the reward id — so chips never auto-appear
    /// and a double-tap can't double-pay. Money-safe by the same rules as the rest of the wallet path.
    ///
    /// A row is ONE reward line (<see cref="PlayerReward.Kind"/> + its target), so the inbox can carry anything the
    /// game gives out, not just currency.
    /// </summary>
    public sealed class RewardService : IRewardService
    {
        private readonly AppDbContext _db;
        private readonly IWalletService _wallet;
        private readonly IRewardGrantService _grants;
        private readonly ILogger<RewardService> _logger;

        public RewardService(AppDbContext db, IWalletService wallet, IRewardGrantService grants, ILogger<RewardService> logger)
        {
            _db = db; _wallet = wallet; _grants = grants; _logger = logger;
        }

        public Task GrantAsync(Guid userId, RewardSource source, CurrencyType currency, decimal amount,
            string title, string idemKey, string metadataJson = null, DateTime? expiresAt = null)
            => GrantLineAsync(userId, source, RewardGrant.Currency(currency.ToString(), amount), title, idemKey, metadataJson, expiresAt);

        public async Task GrantLineAsync(Guid userId, RewardSource source, RewardGrant line, string title,
            string idemKey, string metadataJson = null, DateTime? expiresAt = null)
        {
            if (line == null || line.Amount <= 0m || string.IsNullOrEmpty(idemKey)) return;

            // Currency lines resolve their enum up front so the stored row keeps the exact historical shape
            // (Currency + Amount) and a bad currency name is rejected at ENQUEUE time rather than at claim time.
            var currency = CurrencyType.Chips;
            if (line.Kind == RewardKind.Currency && !RewardCurrencies.TryParseAllowed(line.Id, out currency))
            {
                _logger.LogError("Reward NOT enqueued: '{Id}' is not a grantable currency (idemKey {Key}).", line.Id, idemKey);
                return;
            }

            // Idempotent enqueue: the unique IdempotencyKey index guarantees once. Cheap pre-check + catch the race.
            if (await _db.PlayerRewards.AnyAsync(r => r.IdempotencyKey == idemKey)) return;

            _db.PlayerRewards.Add(new PlayerReward
            {
                UserId = userId,
                Source = source,
                Kind = line.Kind,
                ItemId = line.Kind == RewardKind.Currency ? null : line.Id,
                Currency = currency,
                Amount = line.Amount,
                Title = title,
                MetadataJson = metadataJson,
                IdempotencyKey = idemKey,
                ExpiresAt = expiresAt,
                Status = RewardStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            });
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateException) { _db.ChangeTracker.Clear(); /* concurrent insert hit the unique key — already enqueued */ }
        }

        public async Task<List<RewardDto>> GetPendingAsync(Guid userId)
        {
            var now = DateTime.UtcNow;
            return await _db.PlayerRewards.AsNoTracking()
                .Where(r => r.UserId == userId && r.Status == RewardStatus.Pending && (r.ExpiresAt == null || r.ExpiresAt > now))
                .OrderBy(r => r.CreatedAt)
                .Select(r => new RewardDto
                {
                    Id = r.Id.ToString(),
                    Source = (int)r.Source,
                    Title = r.Title,
                    Kind = (int)r.Kind,
                    ItemId = r.ItemId,
                    Currency = (int)r.Currency,
                    Amount = r.Amount,
                    CreatedAt = r.CreatedAt,
                    ExpiresAt = r.ExpiresAt,
                })
                .ToListAsync();
        }

        public async Task<ClaimResultDto> ClaimAsync(Guid userId, Guid rewardId)
        {
            var reward = await _db.PlayerRewards.FirstOrDefaultAsync(r => r.Id == rewardId && r.UserId == userId);
            if (reward == null) return Fail("Reward not found.");
            if (reward.Status == RewardStatus.Claimed) return new ClaimResultDto { Ok = true, ClaimedCount = 0, NewChipBalance = await ChipsAsync(userId) };
            if (reward.Status == RewardStatus.Expired || (reward.ExpiresAt.HasValue && reward.ExpiresAt.Value <= DateTime.UtcNow))
                return Fail("Reward expired.");

            var granted = await GrantAndMarkAsync(userId, reward);
            return new ClaimResultDto { Ok = true, ClaimedCount = 1, Granted = granted, NewChipBalance = await ChipsAsync(userId) };
        }

        public async Task<ClaimResultDto> ClaimAllAsync(Guid userId)
        {
            var now = DateTime.UtcNow;
            var pending = await _db.PlayerRewards
                .Where(r => r.UserId == userId && r.Status == RewardStatus.Pending && (r.ExpiresAt == null || r.ExpiresAt > now))
                .ToListAsync();

            int count = 0;
            var granted = new List<GrantedLineDto>();
            foreach (var reward in pending)
            {
                try { granted.AddRange(await GrantAndMarkAsync(userId, reward)); count++; }
                catch (Exception ex) { _logger.LogError(ex, "Claim-all: reward {RewardId} failed (others continue)", reward.Id); }
            }
            return new ClaimResultDto { Ok = true, ClaimedCount = count, Granted = granted, NewChipBalance = await ChipsAsync(userId) };
        }

        // Pay out FIRST (idempotent on the reward id — a retry/concurrent claim can't double-pay), THEN mark claimed.
        // If the mark fails the reward stays Pending and a re-claim safely re-pays (idempotent) + re-marks, so a reward
        // is never lost and never paid twice. The payout key stays the historical "reward:{id}" form.
        private async Task<List<GrantedLineDto>> GrantAndMarkAsync(Guid userId, PlayerReward reward)
        {
            var line = new RewardGrant
            {
                Kind = reward.Kind,
                Id = reward.Kind == RewardKind.Currency ? reward.Currency.ToString() : reward.ItemId,
                Amount = reward.Amount,
            };
            var granted = await _grants.GrantOneAsync(userId, line, $"reward:{reward.Id:N}", reward.Title ?? "Reward",
                externalRef: reward.IdempotencyKey);

            reward.Status = RewardStatus.Claimed;
            reward.ClaimedAt = DateTime.UtcNow;
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); /* a concurrent claim won; the payout was idempotent so no double-pay */ }
            return granted;
        }

        private async Task<decimal> ChipsAsync(Guid userId) => await _wallet.GetBalanceAsync(userId.ToString(), CurrencyType.Chips);

        private static ClaimResultDto Fail(string error) => new ClaimResultDto { Ok = false, Error = error };
    }
}
