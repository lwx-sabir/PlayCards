using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Pass;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Ads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Pass
{
    /// <summary>Where a player is in the current cycle, plus the ad catch-up terms (see <c>IPassService</c>).</summary>
    public sealed class PassCycleRef
    {
        public string PassKey { get; set; }
        public string CycleKey { get; set; }
        public DateTime LocalDate { get; set; }
        public int MaxNode { get; set; }
        public bool IsGolden { get; set; }
        public int AdsPerCatchUp { get; set; }
        public int MaxAdCatchUpsPerCycle { get; set; }
        public PassAvailability Availability { get; set; }
    }

    public interface IPassAdService
    {
        /// <summary>Issue a single-use token for "I'm about to watch an ad to unlock day N". Refuses days that aren't
        /// ad-unlockable, so a client can't manufacture an intent for a day it shouldn't reach.</summary>
        Task<PassAdIntentDto> CreateIntentAsync(Guid userId, string passKey, int node);

        /// <summary>Credit ONE verified ad view. Called only by the ad network's signed callback.</summary>
        Task<(bool Ok, string Error)> CreditAsync(AdSsvCallback callback, CancellationToken ct = default);
    }

    /// <summary>
    /// The rewarded-ad path that lets a free player buy back a missed day (docs/PASS_SPEC.md §5.6).
    ///
    /// The rule that shapes everything here: **the client is never believed.** A credit exists only when the ad
    /// NETWORK tells us server-to-server, with a signature we verify, carrying a token WE issued. Even then it is only
    /// a credit — the day itself is still claimed through the normal, idempotent claim path, which consumes the
    /// credits in the same step it writes the claim row.
    /// </summary>
    public sealed class PassAdService : IPassAdService
    {
        private readonly AppDbContext _db;
        private readonly IPassService _pass;
        private readonly IAdSsvVerifier _verifier;
        private readonly ILogger<PassAdService> _logger;
        private readonly string _secret;

        public PassAdService(AppDbContext db, IPassService pass, IAdSsvVerifier verifier, IConfiguration config,
            ILogger<PassAdService> logger)
        {
            _db = db; _pass = pass; _verifier = verifier; _logger = logger;
            // Falls back to the JWT secret so a deployment can't accidentally run with an empty signing key — the
            // token only has to be unforgeable by the client, and that key already is.
            _secret = FirstNonEmpty(
                config.GetValue<string>("Ads:IntentSecret"),
                config.GetValue<string>("JwtSettings:SecretKey"),
                config.GetValue<string>("Jwt:Key"));
        }

        public async Task<PassAdIntentDto> CreateIntentAsync(Guid userId, string passKey, int node)
        {
            var cycle = await _pass.CurrentCycleAsync(userId, passKey);
            if (cycle == null) return Fail("No active pass.");
            if (string.IsNullOrWhiteSpace(_secret)) { _logger.LogError("Ads:IntentSecret is not configured."); return Fail("Ads are not available right now."); }

            var av = cycle.Availability;
            if (av == null || !av.AdUnlockable.Contains(node))
            {
                // Name the actual reason: a claimed day, today's (already free), or one past the cycle's ad cap.
                if (av != null && av.Claimable.Contains(node)) return Fail("That day is already free to claim.");
                if (av != null && av.AdUnlocksLeft == 0 && cycle.MaxAdCatchUpsPerCycle > 0)
                    return Fail("You've used every ad catch-up this month — Golden unlocks the rest.");
                return Fail("That day can't be unlocked with ads.");
            }

            int held = await UnspentAsync(userId, cycle);
            var token = PassAdTokens.Issue(userId, cycle.PassKey, cycle.CycleKey, node, _secret, DateTime.UtcNow, Guid.NewGuid().ToString("N"));

            return new PassAdIntentDto
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
                _logger.LogWarning("Ad SSV rejected ({Provider}): {Error}", _verifier.Provider, verifyError);
                return (false, verifyError ?? "Callback did not verify.");
            }

            // 2. OUR token, round-tripped through the client and the network. This is what binds the view to a player,
            //    a cycle and a day — the callback's own user_id field is not trusted for that.
            var intent = PassAdTokens.Verify(callback.CustomData, _secret, DateTime.UtcNow, out var tokenError);
            if (intent == null)
            {
                _logger.LogWarning("Ad SSV token rejected: {Error}", tokenError);
                return (false, tokenError);
            }

            var transactionId = callback.TransactionId;
            if (string.IsNullOrWhiteSpace(transactionId)) return (false, "Callback has no transaction id.");

            // 3. The cycle must still be the one the token was issued for — a token held across a month rollover
            //    must not credit the new cycle.
            var cycle = await _pass.CurrentCycleAsync(intent.UserId, intent.PassKey);
            if (cycle == null) return (false, "No active pass.");
            if (!string.Equals(cycle.CycleKey, intent.CycleKey, StringComparison.OrdinalIgnoreCase))
                return (false, "That ad was for a previous cycle.");

            // 4. Cap the credits a player can hold this cycle. Beyond it we log and no-op: ad-farming should be
            //    pointless, not an error the network keeps retrying.
            int held = await UnspentAsync(intent.UserId, cycle);
            int maxHeld = Math.Max(0, cycle.AdsPerCatchUp * cycle.MaxAdCatchUpsPerCycle);
            if (maxHeld == 0 || held >= maxHeld)
            {
                _logger.LogInformation("Ad credit ignored (cap {Max} reached) for user {UserId} cycle {Cycle}", maxHeld, intent.UserId, cycle.CycleKey);
                return (true, null);
            }

            // 5. Write the credit. The unique index on AdTransactionId is what makes a replayed callback a no-op.
            _db.PlayerPassAdUnlocks.Add(new PlayerPassAdUnlock
            {
                UserId = intent.UserId,
                PassKey = cycle.PassKey,
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

            _logger.LogInformation("Ad credit granted: user {UserId} pass {PassKey} cycle {Cycle} node hint {Node} ({Held}/{Max})",
                intent.UserId, cycle.PassKey, cycle.CycleKey, intent.Node, held + 1, maxHeld);
            return (true, null);
        }

        private async Task<int> UnspentAsync(Guid userId, PassCycleRef cycle)
            => await _db.PlayerPassAdUnlocks.AsNoTracking().CountAsync(a =>
                a.UserId == userId && a.PassKey == cycle.PassKey && a.CycleKey == cycle.CycleKey && a.SpentOnNode == null);

        private static PassAdIntentDto Fail(string error) => new PassAdIntentDto { Ok = false, Error = error };

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return null;
        }
    }
}
