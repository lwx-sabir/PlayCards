using System;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Daily;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Ads;
using Khela.Game.Services.Pass;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Daily
{
    public interface IDailyAdService
    {
        /// <summary>Issue a single-use token for "I'm about to watch an ad to unlock day N". Refuses days that aren't
        /// ad-unlockable, so a client can't manufacture an intent for a day it shouldn't reach.</summary>
        Task<DailyAdIntentDto> CreateIntentAsync(Guid userId, int node);

        /// <summary>Credit ONE verified ad view. Called only by the ad network's signed callback.</summary>
        Task<(bool Ok, string Error)> CreditAsync(AdSsvCallback callback, CancellationToken ct = default);
    }

    /// <summary>
    /// The rewarded-ad path that lets a player buy back a missed daily reward.
    ///
    /// Same rule as the pass: **the client is never believed**. A credit exists only when the ad NETWORK tells us
    /// server-to-server, with a signature we verify, carrying a token WE issued. Even then it is only a credit — the
    /// day itself is still claimed through the normal, idempotent claim path, which consumes the credits in the same
    /// step it writes the claim row.
    ///
    /// The token scheme is shared with the pass (<see cref="PassAdTokens"/>) rather than reimplemented: it is signed,
    /// single-use, and already carries a scope field. Passing <see cref="Scope"/> as that scope is what stops a token
    /// minted for the pass from ever crediting the daily ladder, or the reverse.
    /// </summary>
    public sealed class DailyAdService : IDailyAdService
    {
        /// <summary>The token scope for this ladder. Deliberately not a pass key — nothing may match it by accident.</summary>
        public const string Scope = "daily";

        private readonly AppDbContext _db;
        private readonly IDailyService _daily;
        private readonly IAdSsvVerifier _verifier;
        private readonly ILogger<DailyAdService> _logger;
        private readonly string _secret;

        public DailyAdService(AppDbContext db, IDailyService daily, IAdSsvVerifier verifier, IConfiguration config,
            ILogger<DailyAdService> logger)
        {
            _db = db; _daily = daily; _verifier = verifier; _logger = logger;
            // Falls back to the JWT secret so a deployment can't run with an empty signing key — the token only has to
            // be unforgeable by the client, and that key already is.
            _secret = FirstNonEmpty(
                config.GetValue<string>("Ads:IntentSecret"),
                config.GetValue<string>("JwtSettings:SecretKey"),
                config.GetValue<string>("Jwt:Key"));
        }

        public async Task<DailyAdIntentDto> CreateIntentAsync(Guid userId, int node)
        {
            var cycle = await _daily.CurrentCycleAsync(userId);
            if (cycle == null) return Fail("No daily reward is running.");
            if (string.IsNullOrWhiteSpace(_secret))
            {
                _logger.LogError("Ads:IntentSecret is not configured.");
                return Fail("Ads are not available right now.");
            }

            var av = cycle.Availability;
            if (av == null || !av.AdUnlockable.Contains(node))
            {
                // Name the actual reason: an already-free day, one already taken, or one past the per-cycle cap.
                if (av != null && av.Claimable.Contains(node)) return Fail("That day is already free to collect.");
                if (av != null && av.AdUnlocksLeft == 0 && cycle.MaxAdCatchUpsPerCycle > 0)
                    return Fail("You've used every ad catch-up for this run.");
                return Fail("That day can't be unlocked with ads.");
            }

            int held = await UnspentAsync(userId, cycle.CycleKey);
            var token = PassAdTokens.Issue(userId, Scope, cycle.CycleKey, node, _secret,
                DateTime.UtcNow, Guid.NewGuid().ToString("N"));

            return new DailyAdIntentDto
            {
                Ok = true,
                Token = token,
                Node = node,
                AdsRequired = cycle.AdsPerCatchUp,
                CreditsHeld = held,
                ExpiresUtc = DateTime.UtcNow.Add(PassAdTokens.Lifetime),
            };
        }

        public async Task<(bool Ok, string Error)> CreditAsync(AdSsvCallback callback, CancellationToken ct = default)
        {
            if (callback == null) return (false, "Empty callback.");

            // 1. The network's signature. Everything else is worthless without it, so it goes first and fails closed.
            var (verified, verifyError) = await _verifier.VerifyAsync(callback, ct);
            if (!verified)
            {
                _logger.LogWarning("Daily ad SSV rejected ({Provider}): {Error}", _verifier.Provider, verifyError);
                return (false, verifyError ?? "Callback did not verify.");
            }

            // 2. OUR token, round-tripped through the client and the network. This binds the view to a player, a run
            //    and a day — the callback's own user_id field is not trusted for that.
            var intent = PassAdTokens.Verify(callback.CustomData, _secret, DateTime.UtcNow, out var tokenError);
            if (intent == null)
            {
                _logger.LogWarning("Daily ad SSV token rejected: {Error}", tokenError);
                return (false, tokenError);
            }
            if (!string.Equals(intent.PassKey, Scope, StringComparison.Ordinal))
                return (false, "That ad was not for the daily reward.");

            var transactionId = callback.TransactionId;
            if (string.IsNullOrWhiteSpace(transactionId)) return (false, "Callback has no transaction id.");

            // 3. The run must still be the one the token was issued for — a token held across a rollover must not
            //    credit the new run.
            var cycle = await _daily.CurrentCycleAsync(intent.UserId);
            if (cycle == null) return (false, "No daily reward is running.");
            if (!string.Equals(cycle.CycleKey, intent.CycleKey, StringComparison.OrdinalIgnoreCase))
                return (false, "That ad was for a previous run.");

            // 4. Cap the credits a player can hold. Beyond it we log and no-op: ad-farming should be pointless, not
            //    an error the network keeps retrying.
            int held = await UnspentAsync(intent.UserId, cycle.CycleKey);
            int maxHeld = Math.Max(0, cycle.AdsPerCatchUp * cycle.MaxAdCatchUpsPerCycle);
            if (maxHeld == 0 || held >= maxHeld)
            {
                _logger.LogInformation("Daily ad credit ignored (cap {Max} reached) for user {UserId} run {Cycle}",
                    maxHeld, intent.UserId, cycle.CycleKey);
                return (true, null);
            }

            // 5. Write the credit. The unique index on AdTransactionId is what makes a replayed callback a no-op.
            _db.PlayerDailyAdUnlocks.Add(new PlayerDailyAdUnlock
            {
                UserId = intent.UserId,
                CycleKey = cycle.CycleKey,
                AdTransactionId = transactionId.Length > 128 ? transactionId.Substring(0, 128) : transactionId,
                Network = _verifier.Provider,
            });
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();
                return (true, null);   // already credited — a replay, not a failure
            }

            _logger.LogInformation("Daily ad credit granted: user {UserId} run {Cycle} day hint {Node} ({Held}/{Max})",
                intent.UserId, cycle.CycleKey, intent.Node, held + 1, maxHeld);
            return (true, null);
        }

        private async Task<int> UnspentAsync(Guid userId, string cycleKey)
            => await _db.PlayerDailyAdUnlocks.AsNoTracking()
                .CountAsync(a => a.UserId == userId && a.CycleKey == cycleKey && a.SpentOnNode == null);

        private static DailyAdIntentDto Fail(string error) => new DailyAdIntentDto { Ok = false, Error = error };

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return null;
        }
    }
}
