using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Daily;
using Khela.Common.Rewards;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Pass;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Rewards;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Khela.Game.Services.Daily
{
    /// <summary>Where a player is in their run, plus the ad terms — so the ad path doesn't re-derive any of it.</summary>
    public sealed class DailyCycleRef
    {
        public string CycleKey { get; set; }
        public int CycleIndex { get; set; }
        public DateTime LocalDate { get; set; }
        public int DayIndex { get; set; }
        public int AdsPerCatchUp { get; set; }
        public int MaxAdCatchUpsPerCycle { get; set; }
        public DailyAvailability Availability { get; set; }
    }

    public interface IDailyService
    {
        /// <summary>The ladder as this player sees it right now.</summary>
        Task<DailyStateDto> GetStateAsync(Guid userId);

        /// <summary>
        /// Claim a day. <paramref name="node"/> null takes today. <paramref name="useAds"/> spends verified
        /// rewarded-ad credits on a missed day — only ever after the network's callback credited them.
        /// </summary>
        Task<DailyClaimResultDto> ClaimAsync(Guid userId, int? node = null, bool useAds = false);

        /// <summary>Which run this player is in right now, plus the ad catch-up terms.</summary>
        Task<DailyCycleRef> CurrentCycleAsync(Guid userId);
    }

    /// <summary>
    /// The daily login runtime. Every decision it makes comes from <see cref="DailyCatalog"/> (pure, testable); this
    /// class only reads state, writes the claim ledger and hands payloads to <see cref="IRewardGrantService"/>.
    ///
    /// Money-safety shape: RESERVE the claim row, SPEND any ad credits, GRANT, then COMPLETE — all inside ONE
    /// transaction, which <see cref="IWalletService"/> joins rather than opening its own per credit. The ordering and
    /// the idempotency keys still stand behind it (a claim row with <c>CompletedAt == null</c> is re-driven by the
    /// next call, and every granter is keyed so re-running pays nothing twice), but with the whole payout atomic
    /// those are now a belt to the transaction's braces rather than the only guarantee.
    /// </summary>
    public sealed class DailyService : IDailyService
    {
        private readonly AppDbContext _db;
        private readonly IRewardGrantService _grants;
        private readonly IWalletService _wallet;
        private readonly IRedisService _redis;
        private readonly ILogger<DailyService> _logger;
        private readonly IOptionsMonitor<RewardOptions> _rewardOptions;

        public DailyService(AppDbContext db, IRewardGrantService grants, IWalletService wallet, IRedisService redis,
            ILogger<DailyService> logger, IOptionsMonitor<RewardOptions> rewardOptions)
        {
            _db = db; _grants = grants; _wallet = wallet; _redis = redis; _logger = logger;
            _rewardOptions = rewardOptions;
        }

        /// <summary>Missed days free? Redis first, appsettings second — see RewardSwitches. Read per call, so the
        /// admin toggle takes effect on the very next claim rather than the next restart.</summary>
        private Task<bool> BypassAdsAsync() => RewardSwitches.BypassAdForMissedDaysAsync(_redis, _rewardOptions);

        // ---------------- reads ----------------

        public async Task<DailyStateDto> GetStateAsync(Guid userId)
        {
            var ctx = await LoadAsync(userId);
            if (ctx == null) return new DailyStateDto { Active = false };

            var claims = await ClaimsAsync(userId, ctx.CycleKey);
            var claimedNodes = new HashSet<int>(claims.Select(c => c.Node));
            int adUnlocksUsed = claims.Count(c => c.WasAdUnlock);
            int creditsHeld = await UnspentAdCreditsAsync(userId, ctx.CycleKey);
            bool bypass = await BypassAdsAsync();

            var availability = DailyCatalog.Availability(ctx.Config, ctx.DayIndex, claimedNodes, adUnlocksUsed, bypass);

            var dto = new DailyStateDto
            {
                Active = true,
                Title = ctx.Config.Title,
                CycleIndex = ctx.CycleIndex,
                CycleKey = ctx.CycleKey,
                Days = ctx.Config.Days,
                DayIndex = availability.DayIndex,
                MaxNode = availability.MaxNode,
                NextDayUtc = PassClock.NextLocalMidnightUtc(ctx.NowUtc, ctx.Tz),
                CycleEndUtc = PassClock.ToUtc(ctx.StartLocalDate.AddDays(ctx.Config.Days), ctx.Tz),
                TimeZoneId = ctx.Tz.Id,
                AdsPerUnlock = availability.AdsPerUnlock,
                AdUnlocksLeft = availability.AdUnlocksLeft,
                AdCreditsHeld = creditsHeld,
                AdsBypassed = bypass,
            };

            foreach (var node in ctx.Config.Nodes.OrderBy(n => n.Index))
            {
                dto.Nodes.Add(new DailyNodeDto
                {
                    Index = node.Index,
                    IsMilestone = node.IsMilestone,
                    Rewards = node.Rewards ?? new List<RewardGrant>(),
                    // Always send a headline: authored if the admin wrote one, else derived here, so every client
                    // shows the same string instead of each inventing its own formatting.
                    Text = string.IsNullOrWhiteSpace(node.Text) ? PassCatalog.AutoLabel(node.Rewards) : node.Text,
                    Claimed = claimedNodes.Contains(node.Index),
                    ClaimableNow = availability.Claimable.Contains(node.Index),
                    AdUnlockable = availability.AdUnlockable.Contains(node.Index),
                    Missed = availability.Missed.Contains(node.Index),
                });
            }
            return dto;
        }

        public async Task<DailyCycleRef> CurrentCycleAsync(Guid userId)
        {
            var ctx = await LoadAsync(userId);
            if (ctx == null) return null;

            var claims = await ClaimsAsync(userId, ctx.CycleKey);
            bool bypass = await BypassAdsAsync();
            return new DailyCycleRef
            {
                CycleKey = ctx.CycleKey,
                CycleIndex = ctx.CycleIndex,
                LocalDate = ctx.LocalDate,
                DayIndex = ctx.DayIndex,
                AdsPerCatchUp = ctx.Config.AdsPerCatchUp,
                MaxAdCatchUpsPerCycle = ctx.Config.MaxAdCatchUpsPerCycle,
                Availability = DailyCatalog.Availability(ctx.Config, ctx.DayIndex,
                    new HashSet<int>(claims.Select(c => c.Node)), claims.Count(c => c.WasAdUnlock), bypass),
            };
        }

        // ---------------- claim ----------------

        /// <summary>
        /// Claim a day, in ONE database transaction.
        ///
        /// The transaction is here for latency as much as for correctness. Every statement to the database is a round
        /// trip, and against anything but a local server that trip — not the work — is the cost of a claim: reserving,
        /// paying two currencies and completing used to open and commit three separate transactions, ~21 trips in all,
        /// which is over five seconds at 240ms each. Sharing one transaction lets the wallet join it instead of
        /// opening its own (see WalletService.ApplyAsync), and makes the payout properly atomic on the way.
        /// </summary>
        public async Task<DailyClaimResultDto> ClaimAsync(Guid userId, int? node = null, bool useAds = false)
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            var result = await ClaimCoreAsync(userId, node, useAds);
            await tx.CommitAsync();   // a throw leaves it uncommitted: nothing reserved, nothing paid, tap again
            return result;
        }

        private async Task<DailyClaimResultDto> ClaimCoreAsync(Guid userId, int? node, bool useAds)
        {
            var ctx = await LoadAsync(userId);
            if (ctx == null) return Fail("No daily reward is running.");

            var claims = await ClaimsAsync(userId, ctx.CycleKey);
            var claimedNodes = new HashSet<int>(claims.Select(c => c.Node));
            int adUnlocksUsed = claims.Count(c => c.WasAdUnlock);
            var availability = DailyCatalog.Availability(ctx.Config, ctx.DayIndex, claimedNodes, adUnlocksUsed, await BypassAdsAsync());

            // Re-drive an interrupted claim before anything else: a row with CompletedAt == null still owes a payload.
            var pending = claims.FirstOrDefault(c => c.CompletedAt == null && (node == null || c.Node == node.Value));
            if (pending != null) return await GrantAndCompleteAsync(ctx, pending, new DailyClaimResultDto { Ok = true });

            int target = node ?? (availability.Claimable.Count > 0 ? availability.Claimable[0] : 0);
            if (target <= 0) return Fail("Nothing to claim right now.");
            if (ctx.Config.Node(target) == null) return Fail("That day isn't part of the ladder.");
            if (claimedNodes.Contains(target))
            {
                // Refused, but the day IS collected — say so explicitly. A duplicate request is not a mistake to be
                // punished: it is a re-tap, a retry after a timeout, or a queue replayed on reconnect, and telling the
                // client only "refused" makes it un-collect a day the player really does own.
                var already = Fail("You've already collected that day.");
                already.AlreadyClaimed = true;
                already.ClaimedNodes.Add(target);
                return already;
            }

            bool spendAds = false;
            int adCost = 0;

            if (!availability.Claimable.Contains(target))
            {
                if (!availability.AdUnlockable.Contains(target))
                    return Fail(target > availability.MaxNode
                        ? "That day hasn't arrived yet."
                        : "That day can't be collected any more.");

                // A missed day, and the player is paying for it. Ads are never taken on trust: the credits must
                // already exist, written by the network's verified callback.
                if (!useAds) return Fail("That day needs a rewarded ad to unlock.");
                adCost = Math.Max(1, ctx.Config.AdsPerCatchUp);
                int held = await UnspentAdCreditsAsync(userId, ctx.CycleKey);
                if (held < adCost) return Fail($"Watch {adCost - held} more ad(s) to unlock that day.");
                spendAds = true;
            }

            // RESERVE. The unique (user, cycle, node) index makes a concurrent double-claim impossible; losing that
            // race is not an error — reload the winner's row and finish driving it.
            var claim = new PlayerDailyClaim
            {
                UserId = userId,
                CycleKey = ctx.CycleKey,
                Node = target,
                ClaimedOnUtc = ctx.NowUtc,
                ClaimedOnLocalDate = ctx.LocalDate,
                TimeZoneId = ctx.Tz.Id,
                WasAdUnlock = spendAds,
            };
            _db.PlayerDailyClaims.Add(claim);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();
                var winner = await _db.PlayerDailyClaims
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.CycleKey == ctx.CycleKey && c.Node == target);
                if (winner == null) return Fail("Claim failed, try again.");
                return await GrantAndCompleteAsync(ctx, winner, new DailyClaimResultDto { Ok = true });
            }

            var result = new DailyClaimResultDto { Ok = true };

            // SPEND the ad credits BEFORE paying out: if the grant then fails, the retry re-drives this same claim row
            // and the credits are already tied to this day, so the player is never charged twice for one reward.
            if (spendAds) result.AdCreditsSpent = await SpendAdCreditsAsync(userId, ctx.CycleKey, target, adCost);

            return await GrantAndCompleteAsync(ctx, claim, result);
        }

        /// <summary>
        /// Pay the day's payload and close the claim. Re-entrant by design: the granters are keyed on the claim's own
        /// identity, so driving this twice pays once.
        /// </summary>
        private async Task<DailyClaimResultDto> GrantAndCompleteAsync(DailyContext ctx, PlayerDailyClaim claim,
            DailyClaimResultDto result)
        {
            var node = ctx.Config.Node(claim.Node);
            if (node == null)
            {
                // The ladder was edited under the player's feet. Don't strand the row.
                claim.CompletedAt = DateTime.UtcNow;
                await SaveQuietlyAsync();
                return Fail("That day is no longer part of the daily reward.");
            }

            var key = $"daily:{claim.CycleKey}:{claim.UserId:N}:{claim.Node}";
            var description = $"{ctx.Config.Title ?? "Daily"} · day {claim.Node}";

            if (!claim.Granted && node.Rewards != null && node.Rewards.Count > 0)
            {
                result.Granted.AddRange(await _grants.GrantAllAsync(claim.UserId, node.Rewards, key, description));
                claim.Granted = true;
            }

            claim.CompletedAt = DateTime.UtcNow;
            await SaveQuietlyAsync();

            if (!result.ClaimedNodes.Contains(claim.Node)) result.ClaimedNodes.Add(claim.Node);

            // The chips balance comes back from the LEDGER LINE the wallet just wrote, not from a fresh read: it was
            // computed under the row lock a moment ago, so asking the database again is a round trip spent learning
            // what we already know. Only a day that pays no chips at all has to ask.
            var chipsLine = result.Granted.LastOrDefault(g => g != null
                && g.Kind == (int)RewardKind.Currency
                && string.Equals(g.Id, nameof(CurrencyType.Chips), StringComparison.OrdinalIgnoreCase)
                && g.Balance > 0m);

            result.NewChipBalance = chipsLine?.Balance
                ?? await _wallet.GetBalanceAsync(claim.UserId.ToString(), CurrencyType.Chips);
            return result;
        }

        // ---------------- ad credits ----------------

        /// <summary>Mark up to <paramref name="count"/> unspent credits as spent on a day. Returns how many were
        /// actually consumed — the caller has already checked there are enough.</summary>
        private async Task<int> SpendAdCreditsAsync(Guid userId, string cycleKey, int node, int count)
        {
            if (count <= 0) return 0;
            var credits = await _db.PlayerDailyAdUnlocks
                .Where(a => a.UserId == userId && a.CycleKey == cycleKey && a.SpentOnNode == null)
                .OrderBy(a => a.CreatedAt)
                .Take(count)
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var c in credits) { c.SpentOnNode = node; c.SpentAt = now; }
            await SaveQuietlyAsync();
            return credits.Count;
        }

        private async Task<int> UnspentAdCreditsAsync(Guid userId, string cycleKey)
            => await _db.PlayerDailyAdUnlocks.AsNoTracking()
                .CountAsync(a => a.UserId == userId && a.CycleKey == cycleKey && a.SpentOnNode == null);

        // Process-wide, because DailyService is scoped per request and a per-request cache would never get a hit.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Tz, DateTime At)> TzCache
            = new System.Collections.Concurrent.ConcurrentDictionary<Guid, (string, DateTime)>();

        private static readonly TimeSpan TzCacheLife = TimeSpan.FromMinutes(10);

        private async Task<string> TimeZoneIdAsync(Guid userId)
        {
            if (TzCache.TryGetValue(userId, out var hit) && DateTime.UtcNow - hit.At < TzCacheLife) return hit.Tz;

            var tzId = await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId)
                .Select(p => p.TimeZoneId).FirstOrDefaultAsync();

            TzCache[userId] = (tzId, DateTime.UtcNow);
            return tzId;
        }

        private async Task<List<PlayerDailyClaim>> ClaimsAsync(Guid userId, string cycleKey)
            => await _db.PlayerDailyClaims.Where(c => c.UserId == userId && c.CycleKey == cycleKey).ToListAsync();

        // ---------------- context ----------------

        /// <summary>
        /// Resolve the config, the player's timezone, and WHICH RUN they are on — creating or rolling the anchor row
        /// as needed.
        ///
        /// The rollover rule is deliberate: when a run's last day passes, the next run starts on the day the player
        /// NEXT appears, not on the day the old one ended. Someone who disappears for three months comes back to day 1
        /// with a fresh ladder, rather than to day 14 of a run they were never present for.
        /// </summary>
        private async Task<DailyContext> LoadAsync(Guid userId)
        {
            var cfg = await EffectiveAsync();
            if (cfg == null || !cfg.Enabled || cfg.Days == 0) return null;

            var nowUtc = DateTime.UtcNow;

            // The player's timezone, cached for a few minutes. It is read on EVERY call — state, claim, ad intent —
            // and it changes about never; paying a round trip for it on each claim is a quarter of a second spent
            // asking a question whose answer cannot have moved. A stale entry can only shift which local DAY a claim
            // is stamped with, and only for someone who changed timezone in the last few minutes.
            var tz = PassClock.Resolve(await TimeZoneIdAsync(userId));
            var localDate = PassClock.LocalDate(nowUtc, tz);

            var anchor = await _db.PlayerDailyCycles.FirstOrDefaultAsync(c => c.UserId == userId);
            if (anchor == null)
            {
                anchor = new PlayerDailyCycle
                {
                    UserId = userId,
                    CycleIndex = 1,
                    StartLocalDate = localDate,
                    TimeZoneId = tz.Id,
                };
                _db.PlayerDailyCycles.Add(anchor);
                try
                {
                    await _db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Two first-requests raced; the other one won and its row is the truth.
                    _db.ChangeTracker.Clear();
                    anchor = await _db.PlayerDailyCycles.FirstOrDefaultAsync(c => c.UserId == userId);
                    if (anchor == null) return null;
                }
            }

            int dayIndex = (int)(localDate.Date - anchor.StartLocalDate.Date).TotalDays + 1;

            // A clock that went BACKWARDS (a timezone move, a support edit) must not read as a negative day. Re-anchor
            // rather than serve a nonsense ladder.
            if (dayIndex < 1)
            {
                anchor.StartLocalDate = localDate;
                anchor.TimeZoneId = tz.Id;
                anchor.UpdatedAt = DateTime.UtcNow;
                await SaveQuietlyAsync();
                dayIndex = 1;
            }
            else if (dayIndex > cfg.Days)
            {
                anchor.CycleIndex += 1;
                anchor.StartLocalDate = localDate;
                anchor.TimeZoneId = tz.Id;
                anchor.UpdatedAt = DateTime.UtcNow;
                await SaveQuietlyAsync();
                dayIndex = 1;
            }

            return new DailyContext
            {
                Config = cfg,
                Tz = tz,
                NowUtc = nowUtc,
                LocalDate = localDate,
                StartLocalDate = anchor.StartLocalDate.Date,
                CycleIndex = anchor.CycleIndex,
                CycleKey = CycleKey(anchor.CycleIndex),
                DayIndex = dayIndex,
            };
        }

        /// <summary>The cycle key stored on every claim. Short, stable, and it sorts by run.</summary>
        public static string CycleKey(int cycleIndex) => $"d{cycleIndex}";

        /// <summary>The effective config: the Redis overlay if it parses, else the built-in ladder. Redis being down
        /// must never take the daily reward offline.</summary>
        private async Task<DailyConfig> EffectiveAsync()
        {
            try
            {
                var json = await _redis.GetStringAsync(DailyCatalog.RedisKey);
                if (string.IsNullOrWhiteSpace(json)) return DailyCatalog.Defaults();

                var cfg = DailyCatalog.Parse(json, out var error);
                if (error != null) _logger.LogError("Daily config in Redis is invalid, using defaults: {Error}", error);
                return cfg;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the daily config from Redis; using defaults.");
                return DailyCatalog.Defaults();
            }
        }

        private async Task SaveQuietlyAsync()
        {
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); /* a concurrent claim won; payouts were idempotent */ }
        }

        private static DailyClaimResultDto Fail(string error) => new DailyClaimResultDto { Ok = false, Error = error };

        private sealed class DailyContext
        {
            public DailyConfig Config;
            public TimeZoneInfo Tz;
            public DateTime NowUtc;
            public DateTime LocalDate;
            public DateTime StartLocalDate;
            public int CycleIndex;
            public string CycleKey;
            public int DayIndex;
        }
    }
}
