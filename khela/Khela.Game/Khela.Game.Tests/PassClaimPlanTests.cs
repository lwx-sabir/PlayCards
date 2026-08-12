using System;
using System.Collections.Generic;
using System.Linq;
using Khela.Game.Services.Pass;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the claim DECISION (docs/PASS_SPEC.md §5.2): which day a tap resolves to, whether it costs ad views, and
    /// — when it's refused — a reason specific enough to drive the right UI (subscribe vs watch ads vs come back
    /// tomorrow). Pure; the persistence half (reserve → spend → grant → complete) still needs an integration harness.
    /// </summary>
    public class PassClaimPlanTests
    {
        private static readonly DateTime Sept20 = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        private static DateTime LocalOn(int day) => new DateTime(2026, 9, day, 0, 0, 0, DateTimeKind.Unspecified);

        private static PassCycle Cycle(CatchUpPolicy policy = CatchUpPolicy.GoldenOrAds, int adsPer = 2, int cap = 5)
        {
            var program = PassCatalog.MonthlyProgram();
            program.CatchUp = policy;
            program.AdsPerCatchUp = adsPer;
            program.MaxAdCatchUpsPerCycle = cap;
            return PassCatalog.CurrentCycle(program, Sept20, TimeZoneInfo.Utc);
        }

        private static (PassCycle Cycle, PassAvailability Av) Setup(bool isGolden, ISet<int> claimed = null,
            CatchUpPolicy policy = CatchUpPolicy.GoldenOrAds, int adUnlocksUsed = 0, int cap = 5)
        {
            var cycle = Cycle(policy, cap: cap);
            var av = PassCatalog.Availability(cycle, LocalOn(20), claimed ?? new HashSet<int>(), isGolden, adUnlocksUsed);
            return (cycle, av);
        }

        // ---- the happy paths ----

        [Fact]
        public void NoNodeRequested_ClaimsToday()
        {
            var (cycle, av) = Setup(isGolden: false);
            var d = PassClaimPlan.Decide(cycle, av, null, new HashSet<int>(), useAds: false, adCreditsHeld: 0);
            Assert.True(d.Ok);
            Assert.Equal(20, d.Node);
            Assert.False(d.SpendAds);
        }

        [Fact]
        public void ASubscriberBackfillsAMissedDayForFree()
        {
            var claimed = new HashSet<int> { 20 };                 // today already taken
            var (cycle, av) = Setup(isGolden: true, claimed);
            var d = PassClaimPlan.Decide(cycle, av, 12, claimed, useAds: false, adCreditsHeld: 0);
            Assert.True(d.Ok);
            Assert.Equal(12, d.Node);
            Assert.False(d.SpendAds);                              // Golden never spends ad credits
        }

        [Fact]
        public void AFreePlayerBuysAMissedDayBackWithAds()
        {
            var claimed = new HashSet<int> { 20 };
            var (cycle, av) = Setup(isGolden: false, claimed);
            var d = PassClaimPlan.Decide(cycle, av, 19, claimed, useAds: true, adCreditsHeld: 2);
            Assert.True(d.Ok);
            Assert.Equal(19, d.Node);
            Assert.True(d.SpendAds);
            Assert.Equal(2, d.AdCost);
        }

        // ---- refusals, each with the reason the UI needs ----

        [Fact]
        public void AMissedDayWithoutAdsAsksForAds_NotForMoney()
        {
            var claimed = new HashSet<int> { 20 };
            var (cycle, av) = Setup(isGolden: false, claimed);
            var d = PassClaimPlan.Decide(cycle, av, 19, claimed, useAds: false, adCreditsHeld: 0);
            Assert.False(d.Ok);
            Assert.True(d.NeedsAds);
            Assert.False(d.NeedsGolden);
            Assert.Contains("2 ads", d.Error);
        }

        [Fact]
        public void HalfWatchedAdsAskForTheREMAINDER()
        {
            var claimed = new HashSet<int> { 20 };
            var (cycle, av) = Setup(isGolden: false, claimed);
            var d = PassClaimPlan.Decide(cycle, av, 19, claimed, useAds: true, adCreditsHeld: 1);
            Assert.False(d.Ok);
            Assert.True(d.NeedsAds);
            Assert.Contains("1 more", d.Error);                    // not "watch 2 ads" again
        }

        [Fact]
        public void WithTheAdPathOffAMissedDayAsksForGolden()
        {
            var claimed = new HashSet<int> { 20 };
            var (cycle, av) = Setup(isGolden: false, claimed, cap: 0);   // ad catch-up switched off by config
            var d = PassClaimPlan.Decide(cycle, av, 15, claimed, useAds: true, adCreditsHeld: 99);
            Assert.False(d.Ok);
            Assert.True(d.NeedsGolden);
            Assert.Contains("Golden", d.Error);
        }

        [Fact]
        public void ThePlayerChoosesWhichMissedDayToSpendAdsOn()
        {
            var claimed = new HashSet<int> { 20 };
            var (cycle, av) = Setup(isGolden: false, claimed);
            // The cap limits how MANY days ads can buy back, never which — day 3 and day 19 are equally available.
            foreach (var day in new[] { 3, 19 })
            {
                var d = PassClaimPlan.Decide(cycle, av, day, claimed, useAds: true, adCreditsHeld: 2);
                Assert.True(d.Ok);
                Assert.Equal(day, d.Node);
                Assert.True(d.SpendAds);
            }
        }

        [Fact]
        public void WhenEveryAdCatchUpIsSpentTheMessageSaysSo()
        {
            var claimed = new HashSet<int> { 20 };
            var (cycle, av) = Setup(isGolden: false, claimed, adUnlocksUsed: 5);
            var d = PassClaimPlan.Decide(cycle, av, 19, claimed, useAds: true, adCreditsHeld: 99);
            Assert.False(d.Ok);
            Assert.True(d.NeedsGolden);
            Assert.Contains("used every ad catch-up", d.Error);    // a different problem from "buy Golden"
        }

        [Fact]
        public void TomorrowIsRefused()
        {
            var (cycle, av) = Setup(isGolden: true);
            var d = PassClaimPlan.Decide(cycle, av, 21, new HashSet<int>(), useAds: false, adCreditsHeld: 0);
            Assert.False(d.Ok);
            Assert.Contains("hasn't arrived", d.Error);
        }

        [Fact]
        public void AlreadyClaimedIsRefused()
        {
            var claimed = new HashSet<int> { 20 };
            var (cycle, av) = Setup(isGolden: false, claimed);
            var d = PassClaimPlan.Decide(cycle, av, 20, claimed, useAds: false, adCreditsHeld: 0);
            Assert.False(d.Ok);
            Assert.Equal("Already claimed.", d.Error);
        }

        [Fact]
        public void ADayOutsideTheLadderIsRefused()
        {
            var (cycle, av) = Setup(isGolden: true);
            var d = PassClaimPlan.Decide(cycle, av, 99, new HashSet<int>(), useAds: false, adCreditsHeld: 0);
            Assert.False(d.Ok);
            Assert.Contains("isn't part of this pass", d.Error);
        }

        [Fact]
        public void UnderCatchUpNone_AMissedDayIsGoneForEveryone()
        {
            var claimed = new HashSet<int> { 20 };
            var (cycle, av) = Setup(isGolden: true, claimed, policy: CatchUpPolicy.None);
            var d = PassClaimPlan.Decide(cycle, av, 19, claimed, useAds: true, adCreditsHeld: 99);
            Assert.False(d.Ok);
        }

        [Fact]
        public void NoActiveCycleIsRefusedRatherThanCrashing()
        {
            var d = PassClaimPlan.Decide(null, null, 1, new HashSet<int>(), useAds: false, adCreditsHeld: 0);
            Assert.False(d.Ok);
            Assert.Equal("No active pass.", d.Error);
        }

        // ---- claim-all ----

        [Fact]
        public void ClaimAll_TakesIncompleteRowsFirst_ThenEverythingFree_OldestFirst()
        {
            var (_, av) = Setup(isGolden: true, new HashSet<int> { 1, 2 });
            var order = PassClaimPlan.ClaimAllOrder(av, new[] { 2 });   // node 2 was left half-granted by a crash
            Assert.Equal(order.OrderBy(n => n), order);                 // oldest first
            Assert.Contains(2, order);                                  // the interrupted row is re-driven
            Assert.Contains(3, order);
            Assert.Contains(20, order);
        }

        [Fact]
        public void ClaimAll_NeverSpendsAdCredits()
        {
            var claimed = new HashSet<int> { 20 };
            var (_, av) = Setup(isGolden: false, claimed);
            var order = PassClaimPlan.ClaimAllOrder(av, Array.Empty<int>());
            Assert.Empty(order);                                        // the ad-unlockable days are NOT swept up
            Assert.NotEmpty(av.AdUnlockable);                           // …even though they exist
        }
    }
}
