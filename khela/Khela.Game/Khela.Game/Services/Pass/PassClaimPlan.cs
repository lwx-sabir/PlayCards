using System.Collections.Generic;
using System.Linq;

namespace Khela.Game.Services.Pass
{
    /// <summary>The decision: which node a claim resolves to, what it costs, or why it can't happen.</summary>
    public sealed class ClaimDecision
    {
        public bool Ok { get; set; }
        public string Error { get; set; }        // player-facing, and specific about WHY
        public int Node { get; set; }
        public bool SpendAds { get; set; }       // this claim consumes ad credits
        public int AdCost { get; set; }          // how many
        public bool NeedsGolden { get; set; }    // refused because only a subscription reaches this day (drives the CTA)
        public bool NeedsAds { get; set; }       // refused because the player hasn't watched enough ads yet

        public static ClaimDecision Fail(string error, bool needsGolden = false, bool needsAds = false)
            => new ClaimDecision { Ok = false, Error = error, NeedsGolden = needsGolden, NeedsAds = needsAds };
    }

    /// <summary>
    /// The pure decision half of claiming — "which day, and what does it cost?" — split out of <c>PassService</c> so
    /// the branchy part is unit-testable without a database (the same pattern as LobbyPlan / ShoePlan). The service
    /// keeps only the persistence: reserve the row, spend the credits, grant, complete.
    /// </summary>
    public static class PassClaimPlan
    {
        /// <summary>
        /// Resolve a claim request against what the cycle currently allows.
        /// <paramref name="requestedNode"/> null means "today's node, or the best thing I can claim for free".
        /// </summary>
        public static ClaimDecision Decide(PassCycle cycle, PassAvailability availability, int? requestedNode,
            ISet<int> alreadyClaimed, bool useAds, int adCreditsHeld)
        {
            if (cycle == null || availability == null) return ClaimDecision.Fail("No active pass.");

            int node = requestedNode ?? (availability.Claimable.Count > 0 ? availability.Claimable.Max() : availability.MaxNode);

            if (cycle.Node(node) == null) return ClaimDecision.Fail($"Day {node} isn't part of this pass.");
            if (alreadyClaimed != null && alreadyClaimed.Contains(node)) return ClaimDecision.Fail("Already claimed.");
            if (node > availability.MaxNode) return ClaimDecision.Fail("That day hasn't arrived yet.");

            // Free: today's node, or a missed day this player is entitled to backfill.
            if (availability.Claimable.Contains(node))
                return new ClaimDecision { Ok = true, Node = node };

            // A missed day that ads can buy back.
            if (availability.AdUnlockable.Contains(node))
            {
                int cost = availability.AdsPerUnlock;
                if (!useAds)
                    return ClaimDecision.Fail($"Watch {cost} ads to unlock day {node}.", needsAds: true);
                if (adCreditsHeld < cost)
                    return ClaimDecision.Fail($"Watch {cost - adCreditsHeld} more ad(s) to unlock day {node}.", needsAds: true);
                return new ClaimDecision { Ok = true, Node = node, SpendAds = true, AdCost = cost };
            }

            // Out of ad catch-ups for this cycle reads differently from "this needs Golden" — say which.
            if (availability.GoldenLocked.Contains(node))
                return ClaimDecision.Fail(
                    availability.AdUnlocksLeft == 0 && availability.AdsPerUnlock > 0
                        ? "You've used every ad catch-up this month — Golden unlocks the rest."
                        : "That day has passed — Golden unlocks the days you missed.",
                    needsGolden: true);

            return ClaimDecision.Fail("That day can't be claimed.");
        }

        /// <summary>Nodes a "claim everything free" pass should walk, oldest first: anything left incomplete by an
        /// interrupted claim, then everything currently free. Never ad-gated days — spending credits is an explicit
        /// choice, never a side effect of a bulk tap.</summary>
        public static List<int> ClaimAllOrder(PassAvailability availability, IEnumerable<int> incompleteNodes)
            => (incompleteNodes ?? Enumerable.Empty<int>())
                .Concat(availability?.Claimable ?? new List<int>())
                .Distinct().OrderBy(n => n).ToList();
    }
}
