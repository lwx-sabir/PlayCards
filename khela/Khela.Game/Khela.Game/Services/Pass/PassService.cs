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

        /// <summary>Which cycle this player is in right now (resolved in THEIR timezone) plus the ad catch-up terms —
        /// so the ad path doesn't have to re-derive any of it and drift from the claim path.</summary>
        Task<PassCycleRef> CurrentCycleAsync(Guid userId, string passKey = null);

        /// <summary>
        /// THE entitlement seam: record a subscription window and unlock the days the player missed. Idempotent on
        /// <paramref name="purchaseRef"/> (the store's transaction id), so a replayed receipt or a retried renewal
        /// grants nothing new. Callers: IAP receipt validation (<c>source: "iap"</c>) and the admin panel
        /// (<c>"admin"</c>). There is NO in-game-currency purchase path — Golden is real money only.
        /// </summary>
        Task<PassPurchaseResultDto> GrantGoldenAsync(Guid userId, string passKey, string source, string purchaseRef,
            DateTime startsAt, DateTime expiresAt, string originalTransactionId = null, bool autoRenew = false);

        /// <summary>
        /// Refund / chargeback / admin revoke: close the window. Already-collected rewards are NEVER clawed back (the
        /// ledger is append-only), but golden rewards still sitting UNCOLLECTED in the inbox expire with it.
        /// </summary>
        Task<PassPurchaseResultDto> RevokeGoldenAsync(Guid userId, string passKey, string purchaseRef, string reason);
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

        public async Task<PassCycleRef> CurrentCycleAsync(Guid userId, string passKey = null)
        {
            var ctx = await LoadAsync(userId, passKey);
            if (ctx == null) return null;

            var claims = await ClaimsAsync(userId, ctx.Cycle);
            return new PassCycleRef
            {
                PassKey = ctx.Cycle.PassKey,
                CycleKey = ctx.Cycle.CycleKey,
                LocalDate = ctx.LocalDate,
                MaxNode = ctx.Cycle.MaxNode(ctx.LocalDate),
                IsGolden = ctx.IsGolden,
                AdsPerCatchUp = ctx.Cycle.AdsPerCatchUp,
                MaxAdCatchUpsPerCycle = ctx.Cycle.MaxAdCatchUpsPerCycle,
                Availability = PassCatalog.Availability(ctx.Cycle, ctx.LocalDate,
                    new HashSet<int>(claims.Select(c => c.Node)), ctx.IsGolden, claims.Count(c => c.WasAdUnlock)),
            };
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

            // The whole "which day, and what does it cost" decision is pure and unit-tested (PassClaimPlan); this
            // method only persists the outcome.
            int creditsHeld = useAds ? await UnspentAdCreditsAsync(userId, ctx.Cycle) : 0;
            var decision = PassClaimPlan.Decide(ctx.Cycle, availability, node, claimedNodes, useAds, creditsHeld);
            if (!decision.Ok) return Fail(decision.Error);

            int target = decision.Node;
            bool spendAds = decision.SpendAds;

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
                result.AdCreditsSpent = await SpendAdCreditsAsync(userId, ctx.Cycle, target, decision.AdCost);

            return await GrantAndCompleteAsync(ctx, claim, result);
        }

        public async Task<PassClaimResultDto> ClaimAllAsync(Guid userId, string passKey = null)
        {
            var ctx = await LoadAsync(userId, passKey);
            if (ctx == null) return Fail("No active pass.");

            var claims = await ClaimsAsync(userId, ctx.Cycle);
            var availability = PassCatalog.Availability(ctx.Cycle, ctx.LocalDate,
                new HashSet<int>(claims.Select(c => c.Node)), ctx.IsGolden, claims.Count(c => c.WasAdUnlock));

            var todo = PassClaimPlan.ClaimAllOrder(availability, claims.Where(c => c.CompletedAt == null).Select(c => c.Node));
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

        // ---------------- entitlement (real money) ----------------

        public async Task<PassPurchaseResultDto> GrantGoldenAsync(Guid userId, string passKey, string source, string purchaseRef,
            DateTime startsAt, DateTime expiresAt, string originalTransactionId = null, bool autoRenew = false)
        {
            passKey = string.IsNullOrWhiteSpace(passKey) ? PassCatalog.MonthlyKey : passKey.Trim();
            if (string.IsNullOrWhiteSpace(purchaseRef)) return PurchaseFail("Missing purchase reference.");
            if (expiresAt <= startsAt) return PurchaseFail("The entitlement ends before it starts.");

            // 1. RECORD the window. The unique (user, pass, purchaseRef) index is the idempotency: a replayed receipt
            //    or a retried renewal collides here and falls through to the unlock, which is itself idempotent.
            bool inserted = true;
            var existing = await _db.PlayerPassEntitlements
                .FirstOrDefaultAsync(e => e.UserId == userId && e.PassKey == passKey && e.PurchaseRef == purchaseRef);
            if (existing == null)
            {
                _db.PlayerPassEntitlements.Add(new PlayerPassEntitlement
                {
                    UserId = userId,
                    PassKey = passKey,
                    Source = string.IsNullOrWhiteSpace(source) ? "iap" : source.Trim(),
                    PurchaseRef = purchaseRef.Trim(),
                    OriginalTransactionId = originalTransactionId,
                    StartsAt = startsAt,
                    ExpiresAt = expiresAt,
                    AutoRenew = autoRenew,
                });
                try { await _db.SaveChangesAsync(); }
                catch (DbUpdateException) { _db.ChangeTracker.Clear(); inserted = false; /* concurrent insert won */ }
            }
            else
            {
                inserted = false;
                if (existing.RevokedAt != null)
                {
                    // A resubscribe under the same transaction id: re-open the window rather than leaving them locked out.
                    existing.RevokedAt = null;
                    existing.ExpiresAt = expiresAt > existing.ExpiresAt ? expiresAt : existing.ExpiresAt;
                    existing.AutoRenew = autoRenew;
                    await SaveQuietlyAsync();
                }
            }

            // 2. UNLOCK the missed days of the CURRENT cycle (never an earlier one — an October subscription never
            //    pays out September's ladder).
            int unlocked = await UnlockMissedDaysAsync(userId, passKey);

            var until = await GoldenUntilAsync(userId, passKey, DateTime.UtcNow);
            _logger.LogInformation("Pass golden granted: user {UserId} pass {PassKey} source {Source} ref {Ref} until {Until} (new row: {Inserted}, unlocked {Unlocked})",
                userId, passKey, source, purchaseRef, expiresAt, inserted, unlocked);

            return new PassPurchaseResultDto { Ok = true, IsGolden = until != null, GoldenUntilUtc = until, UnlockedNodes = unlocked };
        }

        public async Task<PassPurchaseResultDto> RevokeGoldenAsync(Guid userId, string passKey, string purchaseRef, string reason)
        {
            passKey = string.IsNullOrWhiteSpace(passKey) ? PassCatalog.MonthlyKey : passKey.Trim();
            var now = DateTime.UtcNow;

            var rows = await _db.PlayerPassEntitlements
                .Where(e => e.UserId == userId && e.PassKey == passKey && e.RevokedAt == null
                         && (purchaseRef == null || e.PurchaseRef == purchaseRef))
                .ToListAsync();
            foreach (var r in rows) r.RevokedAt = now;
            await SaveQuietlyAsync();

            // Uncollected golden rewards die with the entitlement — but only if the player is now actually un-golden
            // (revoking ONE transaction of an overlapping pair must not strip a still-valid subscription).
            int expired = 0;
            var until = await GoldenUntilAsync(userId, passKey, now);
            if (until == null)
            {
                var prefix = $"pass-retro:{passKey}:";
                var pending = await _db.PlayerRewards
                    .Where(r => r.UserId == userId && r.Status == RewardStatus.Pending && r.IdempotencyKey.StartsWith(prefix))
                    .ToListAsync();
                foreach (var p in pending) p.Status = RewardStatus.Expired;
                expired = pending.Count;
                await SaveQuietlyAsync();
            }

            _logger.LogWarning("Pass golden REVOKED: user {UserId} pass {PassKey} ref {Ref} reason {Reason} — {Rows} window(s), {Expired} uncollected reward(s) expired",
                userId, passKey, purchaseRef ?? "(all)", reason, rows.Count, expired);

            return new PassPurchaseResultDto { Ok = true, IsGolden = until != null, GoldenUntilUtc = until };
        }

        /// <summary>
        /// The missed-days unlock — the reason subscribing mid-month is worth it.
        ///
        /// Nodes the player ALREADY claimed without golden get their golden payload ENQUEUED to the reward inbox
        /// (collected by tapping, like every other reward — chips never just appear). Nodes they never claimed need
        /// nothing here: catch-up is free for a subscriber, so they simply become claimable on the normal path, which
        /// keeps ONE payout route and gives the player the collect moment for both halves.
        ///
        /// Idempotent per (pass, cycle, node, line), so a renewal, a retry or a resubscribe can never pay a day twice.
        /// Returns how many days the purchase opened up — the number the UI celebrates.
        /// </summary>
        private async Task<int> UnlockMissedDaysAsync(Guid userId, string passKey)
        {
            var ctx = await LoadAsync(userId, passKey);
            if (ctx == null || !ctx.IsGolden) return 0;

            var claims = await ClaimsAsync(userId, ctx.Cycle);
            var claimedNodes = new HashSet<int>(claims.Select(c => c.Node));
            int maxNode = ctx.Cycle.MaxNode(ctx.LocalDate);
            int unlocked = 0;

            foreach (var claim in claims.Where(c => !c.GoldenGranted && c.Node <= maxNode).OrderBy(c => c.Node))
            {
                var node = ctx.Cycle.Node(claim.Node);
                if (node?.Golden == null || node.Golden.Count == 0) continue;

                for (int i = 0; i < node.Golden.Count; i++)
                    await _rewards.GrantLineAsync(userId, RewardSource.Pass, node.Golden[i],
                        $"{ctx.Cycle.Title ?? "Pass"} · day {claim.Node} (Golden)",
                        $"pass-retro:{ctx.Cycle.PassKey}:{ctx.Cycle.CycleKey}:{userId:N}:{claim.Node}:{i}");

                claim.GoldenGranted = true;
                unlocked++;
            }
            await SaveQuietlyAsync();

            // Days never claimed at all are now freely claimable — count them so the CTA's promise matches reality.
            for (int n = 1; n <= maxNode; n++)
                if (!claimedNodes.Contains(n) && ctx.Cycle.Node(n) != null) unlocked++;

            return unlocked;
        }

        private async Task<DateTime?> GoldenUntilAsync(Guid userId, string passKey, DateTime atUtc)
            => await _db.PlayerPassEntitlements.AsNoTracking()
                .Where(e => e.UserId == userId && e.PassKey == passKey && e.RevokedAt == null
                         && e.StartsAt <= atUtc && e.ExpiresAt > atUtc)
                .OrderByDescending(e => e.ExpiresAt)
                .Select(e => (DateTime?)e.ExpiresAt)
                .FirstOrDefaultAsync();

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

        private static PassPurchaseResultDto PurchaseFail(string error) => new PassPurchaseResultDto { Ok = false, Error = error };
    }
}
