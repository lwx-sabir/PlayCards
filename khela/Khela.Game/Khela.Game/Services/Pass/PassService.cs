using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Pass;
using Khela.Common.Rewards;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Rewards;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Pass
{
    public interface IPassService
    {
        /// <summary>The whole pass screen: the ladder, what's claimable, and the golden state.</summary>
        Task<PassStateDto> GetStateAsync(Guid userId, string passKey = null);

        /// <summary>
        /// Claim ONE node. Omit <paramref name="node"/> for today's. An earlier node is a catch-up: free for
        /// subscribers, or paid for with rewarded-ad credits when <paramref name="useAds"/> is set.
        /// </summary>
        Task<PassClaimResultDto> ClaimAsync(Guid userId, string passKey = null, int? node = null, bool useAds = false);

        /// <summary>Claim everything currently free, oldest first. Never spends ad credits.</summary>
        Task<PassClaimResultDto> ClaimAllAsync(Guid userId, string passKey = null);

        /// <summary>True if an un-revoked subscription window covers <paramref name="atUtc"/>.</summary>
        Task<bool> IsGoldenAsync(Guid userId, string passKey, DateTime atUtc);
    }

    /// <summary>
    /// The pass runtime. Everything it decides comes from <see cref="PassCatalog"/> (pure, unit-tested) — this class
    /// only reads state, writes the claim ledger and hands payloads to <see cref="IRewardGrantService"/>.
    ///
    /// Money-safety shape (docs/PASS_SPEC.md §5.2): RESERVE the claim row, SPEND any ad credits, GRANT, then COMPLETE.
    /// No outer DB transaction wraps the grant — <see cref="IWalletService"/> opens its own per credit — so crash
    /// safety comes from ordering plus idempotency instead: a claim row that exists with <c>CompletedAt == null</c> is
    /// re-driven by the next call, and every granter is keyed so re-running pays nothing twice.
    /// </summary>
    public sealed class PassService : IPassService
    {
        private readonly AppDbContext _db;
        private readonly IRewardGrantService _grants;
        private readonly IRewardService _rewards;
        private readonly IWalletService _wallet;
        private readonly IRedisService _redis;
        private readonly ILogger<PassService> _logger;

        public PassService(AppDbContext db, IRewardGrantService grants, IRewardService rewards, IWalletService wallet,
            IRedisService redis, ILogger<PassService> logger)
        {
            _db = db; _grants = grants; _rewards = rewards; _wallet = wallet; _redis = redis; _logger = logger;
        }

        // ---------------- reads ----------------

        public async Task<PassStateDto> GetStateAsync(Guid userId, string passKey = null)
        {
            var ctx = await LoadAsync(userId, passKey);
            if (ctx == null) return new PassStateDto { Active = false };

            var claims = await ClaimsAsync(userId, ctx.Cycle);
            var claimedNodes = new HashSet<int>(claims.Select(c => c.Node));
            var adUnlocksUsed = claims.Count(c => c.WasAdUnlock);
            int creditsHeld = await UnspentAdCreditsAsync(userId, ctx.Cycle);

            var availability = PassCatalog.Availability(ctx.Cycle, ctx.LocalDate, claimedNodes, ctx.IsGolden, adUnlocksUsed);

            var dto = new PassStateDto
            {
                Active = true,
                PassKey = ctx.Cycle.PassKey,
                CycleKey = ctx.Cycle.CycleKey,
                Title = ctx.Cycle.Title,
                CycleStartUtc = ctx.Cycle.StartUtc,
                CycleEndUtc = ctx.Cycle.EndUtc,
                NextDayUtc = PassClock.NextLocalMidnightUtc(ctx.NowUtc, ctx.Tz),
                TimeZoneId = ctx.Tz.Id,
                DayIndex = availability.DayIndex,
                Days = ctx.Cycle.Days,
                MaxNode = availability.MaxNode,
                IsGolden = ctx.IsGolden,
                GoldenUntilUtc = ctx.GoldenUntilUtc,
                AutoRenew = ctx.AutoRenew,
                GoldenProductIdApple = ctx.Program.GoldenProductIdApple,
                GoldenProductIdGoogle = ctx.Program.GoldenProductIdGoogle,
                GoldenPriceUsd = ctx.Program.GoldenPriceUsd,
                CatchUp = ctx.Cycle.CatchUp.ToString(),
                AdsPerUnlock = availability.AdsPerUnlock,
                AdUnlocksLeft = availability.AdUnlocksLeft,
                AdCreditsHeld = creditsHeld,
                GoldenLockedCount = availability.GoldenLocked.Count,
            };

            var byNode = claims.ToDictionary(c => c.Node);
            foreach (var node in ctx.Cycle.Nodes)
            {
                byNode.TryGetValue(node.Index, out var claim);
                dto.Nodes.Add(new PassNodeDto
                {
                    Index = node.Index,
                    IsMilestone = node.IsMilestone,
                    Free = node.Free ?? new List<RewardGrant>(),
                    Golden = node.Golden ?? new List<RewardGrant>(),
                    Claimed = claim != null,
                    GoldenClaimed = claim?.GoldenGranted ?? false,
                    ClaimableNow = availability.Claimable.Contains(node.Index),
                    AdUnlockable = availability.AdUnlockable.Contains(node.Index),
                    GoldenLocked = availability.GoldenLocked.Contains(node.Index),
                });
            }
            return dto;
        }

        public async Task<bool> IsGoldenAsync(Guid userId, string passKey, DateTime atUtc)
        {
            passKey = passKey ?? PassCatalog.MonthlyKey;
            return await _db.PlayerPassEntitlements.AsNoTracking().AnyAsync(e =>
                e.UserId == userId && e.PassKey == passKey &&
                e.RevokedAt == null && e.StartsAt <= atUtc && e.ExpiresAt > atUtc);
        }

        // ---------------- claiming ----------------

        public async Task<PassClaimResultDto> ClaimAsync(Guid userId, string passKey = null, int? node = null, bool useAds = false)
        {
            var ctx = await LoadAsync(userId, passKey);
            if (ctx == null) return Fail("No active pass.");

            var claims = await ClaimsAsync(userId, ctx.Cycle);
            var claimedNodes = new HashSet<int>(claims.Select(c => c.Node));
            var adUnlocksUsed = claims.Count(c => c.WasAdUnlock);
            var availability = PassCatalog.Availability(ctx.Cycle, ctx.LocalDate, claimedNodes, ctx.IsGolden, adUnlocksUsed);

            // Re-drive an interrupted claim before anything else: a row with CompletedAt == null still owes payloads.
            var pending = claims.FirstOrDefault(c => c.CompletedAt == null && (node == null || c.Node == node.Value));
            if (pending != null) return await GrantAndCompleteAsync(ctx, pending, new PassClaimResultDto { Ok = true });

            int target = node ?? (availability.Claimable.Count > 0 ? availability.Claimable.Max() : availability.MaxNode);

            // Why this node isn't claimable — the message the player actually needs.
            if (ctx.Cycle.Node(target) == null) return Fail($"Day {target} isn't part of this pass.");
            if (claimedNodes.Contains(target)) return Fail("Already claimed.");
            if (target > availability.MaxNode) return Fail("That day hasn't arrived yet.");

            bool spendAds = false;
            if (!availability.Claimable.Contains(target))
            {
                if (!useAds || !availability.AdUnlockable.Contains(target))
                    return Fail(availability.AdUnlockable.Contains(target)
                        ? $"Watch {availability.AdsPerUnlock} ads to unlock day {target}."
                        : "That day has passed — Golden unlocks the days you missed.");

                if (await UnspentAdCreditsAsync(userId, ctx.Cycle) < availability.AdsPerUnlock)
                    return Fail($"Watch {availability.AdsPerUnlock} ads to unlock day {target}.");
                spendAds = true;
            }

            // RESERVE. The unique (user, pass, cycle, node) index is what makes a concurrent double-claim impossible;
            // losing that race is not an error — reload the winner's row and finish driving it.
            var claim = new PlayerPassClaim
            {
                UserId = userId,
                PassKey = ctx.Cycle.PassKey,
                CycleKey = ctx.Cycle.CycleKey,
                Node = target,
                ClaimedOnUtc = ctx.NowUtc,
                ClaimedOnLocalDate = ctx.LocalDate,
                TimeZoneId = ctx.Tz.Id,
                WasAdUnlock = spendAds,
            };
            _db.PlayerPassClaims.Add(claim);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();
                var winner = await _db.PlayerPassClaims.FirstOrDefaultAsync(c =>
                    c.UserId == userId && c.PassKey == ctx.Cycle.PassKey && c.CycleKey == ctx.Cycle.CycleKey && c.Node == target);
                if (winner == null) return Fail("Claim failed, try again.");
                return await GrantAndCompleteAsync(ctx, winner, new PassClaimResultDto { Ok = true });
            }

            var result = new PassClaimResultDto { Ok = true };

            // SPEND the ad credits BEFORE paying out: if the grant then fails, the retry re-drives the same claim row
            // and the credits are already tied to this node, so the player is never charged twice for one day.
            if (spendAds)
                result.AdCreditsSpent = await SpendAdCreditsAsync(userId, ctx.Cycle, target, availability.AdsPerUnlock);

            return await GrantAndCompleteAsync(ctx, claim, result);
        }

        public async Task<PassClaimResultDto> ClaimAllAsync(Guid userId, string passKey = null)
        {
            var ctx = await LoadAsync(userId, passKey);
            if (ctx == null) return Fail("No active pass.");

            var claims = await ClaimsAsync(userId, ctx.Cycle);
            var availability = PassCatalog.Availability(ctx.Cycle, ctx.LocalDate,
                new HashSet<int>(claims.Select(c => c.Node)), ctx.IsGolden, claims.Count(c => c.WasAdUnlock));

            var pendingNodes = claims.Where(c => c.CompletedAt == null).Select(c => c.Node);
            var todo = pendingNodes.Concat(availability.Claimable).Distinct().OrderBy(n => n).ToList();
            if (todo.Count == 0) return Fail("Nothing to claim.");

            var result = new PassClaimResultDto { Ok = true };
            foreach (var n in todo)
            {
                var one = await ClaimAsync(userId, ctx.Cycle.PassKey, n);   // never useAds — claim-all stays free
                if (!one.Ok) continue;                                      // a node that slipped away doesn't stop the rest
                result.ClaimedNodes.AddRange(one.ClaimedNodes);
                result.Granted.AddRange(one.Granted);
                result.NewChipBalance = one.NewChipBalance;
            }
            if (result.ClaimedNodes.Count == 0) return Fail("Nothing to claim.");
            return result;
        }

        // ---------------- internals ----------------

        /// <summary>
        /// Pay out whatever this claim row still owes, then complete it. Free first, then golden if the player is
        /// entitled RIGHT NOW — a lapsed subscriber simply doesn't get the golden half, and <c>GoldenGranted</c> stays
        /// false so a later subscription's missed-days unlock picks the node back up.
        /// </summary>
        private async Task<PassClaimResultDto> GrantAndCompleteAsync(PassContext ctx, PlayerPassClaim claim, PassClaimResultDto result)
        {
            var node = ctx.Cycle.Node(claim.Node);
            if (node == null)
            {
                // The ladder was edited under the player's feet. Don't strand the row.
                claim.CompletedAt = DateTime.UtcNow;
                await SaveQuietlyAsync();
                return Fail("That day is no longer part of this pass.");
            }

            var baseKey = $"pass:{claim.PassKey}:{claim.CycleKey}:{claim.UserId:N}:{claim.Node}";
            var description = $"{ctx.Cycle.Title ?? "Pass"} · day {claim.Node}";

            if (!claim.FreeGranted && node.Free != null && node.Free.Count > 0)
            {
                result.Granted.AddRange(await _grants.GrantAllAsync(claim.UserId, node.Free, $"{baseKey}:free", description));
                claim.FreeGranted = true;
            }

            if (!claim.GoldenGranted && ctx.IsGolden && node.Golden != null && node.Golden.Count > 0)
            {
                result.Granted.AddRange(await _grants.GrantAllAsync(claim.UserId, node.Golden, $"{baseKey}:golden", description));
                claim.GoldenGranted = true;
            }

            claim.CompletedAt = DateTime.UtcNow;
            await SaveQuietlyAsync();

            if (!result.ClaimedNodes.Contains(claim.Node)) result.ClaimedNodes.Add(claim.Node);
            result.NewChipBalance = await _wallet.GetBalanceAsync(claim.UserId.ToString(), CurrencyType.Chips);
            return result;
        }

        /// <summary>Mark up to <paramref name="count"/> unspent credits as spent on a node. Returns how many were
        /// actually consumed — the caller has already checked there are enough.</summary>
        private async Task<int> SpendAdCreditsAsync(Guid userId, PassCycle cycle, int node, int count)
        {
            if (count <= 0) return 0;
            var credits = await _db.PlayerPassAdUnlocks
                .Where(a => a.UserId == userId && a.PassKey == cycle.PassKey && a.CycleKey == cycle.CycleKey && a.SpentOnNode == null)
                .OrderBy(a => a.CreatedAt)
                .Take(count)
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var c in credits) { c.SpentOnNode = node; c.SpentAt = now; }
            await SaveQuietlyAsync();
            return credits.Count;
        }

        private async Task<int> UnspentAdCreditsAsync(Guid userId, PassCycle cycle)
            => await _db.PlayerPassAdUnlocks.AsNoTracking().CountAsync(a =>
                a.UserId == userId && a.PassKey == cycle.PassKey && a.CycleKey == cycle.CycleKey && a.SpentOnNode == null);

        private async Task<List<PlayerPassClaim>> ClaimsAsync(Guid userId, PassCycle cycle)
            => await _db.PlayerPassClaims
                .Where(c => c.UserId == userId && c.PassKey == cycle.PassKey && c.CycleKey == cycle.CycleKey)
                .ToListAsync();

        private async Task SaveQuietlyAsync()
        {
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); /* a concurrent claim won; payouts were idempotent */ }
        }

        /// <summary>Everything a request needs about "this player, this pass, right now".</summary>
        private sealed class PassContext
        {
            public PassProgram Program;
            public PassCycle Cycle;
            public TimeZoneInfo Tz;
            public DateTime NowUtc;
            public DateTime LocalDate;
            public bool IsGolden;
            public DateTime? GoldenUntilUtc;
            public bool AutoRenew;
        }

        private async Task<PassContext> LoadAsync(Guid userId, string passKey)
        {
            var cfg = await EffectiveAsync();
            var program = string.IsNullOrEmpty(passKey) ? cfg.Default() : cfg.Find(passKey);
            if (program == null || !program.Enabled || !cfg.Enabled) return null;

            var nowUtc = DateTime.UtcNow;
            var tzId = await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId)
                .Select(p => p.TimeZoneId).FirstOrDefaultAsync();
            var tz = PassClock.Resolve(tzId);

            var cycle = PassCatalog.CurrentCycle(program, nowUtc, tz);
            if (cycle == null || cycle.Length == 0) return null;

            var entitlement = await _db.PlayerPassEntitlements.AsNoTracking()
                .Where(e => e.UserId == userId && e.PassKey == program.Key && e.RevokedAt == null
                         && e.StartsAt <= nowUtc && e.ExpiresAt > nowUtc)
                .OrderByDescending(e => e.ExpiresAt)
                .FirstOrDefaultAsync();

            return new PassContext
            {
                Program = program,
                Cycle = cycle,
                Tz = tz,
                NowUtc = nowUtc,
                LocalDate = PassClock.LocalDate(nowUtc, tz),
                IsGolden = entitlement != null,
                GoldenUntilUtc = entitlement?.ExpiresAt,
                AutoRenew = entitlement?.AutoRenew ?? false,
            };
        }

        /// <summary>Effective config = the admin override (Redis <c>khela:pass</c>) if it parses, else code defaults.
        /// Read per call (small JSON) so a dashboard save applies on the next request with no restart.</summary>
        private async Task<PassConfig> EffectiveAsync()
        {
            try
            {
                var json = await _redis.GetDatabase().StringGetAsync(PassCatalog.RedisKey);
                if (json.HasValue)
                {
                    var cfg = PassCatalog.TryParse(json);
                    if (cfg != null) return cfg;
                    _logger.LogWarning("khela:pass override is unparseable — falling back to defaults.");
                }
            }
            catch { /* Redis down → defaults; the pass must not depend on the cache being up */ }
            return PassCatalog.Defaults();
        }

        private static PassClaimResultDto Fail(string error) => new PassClaimResultDto { Ok = false, Error = error };
    }
}
