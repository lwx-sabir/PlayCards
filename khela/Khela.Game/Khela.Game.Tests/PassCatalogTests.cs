using System;
using System.Collections.Generic;
using System.Linq;
using Khela.Common.Rewards;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Pass;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the monthly pass catalog (docs/PASS_SPEC.md §3/§5) — the legal guardrail that no reward may ever be the
    /// tradeable token (CLAUDE.md NON-NEGOTIABLE #2/#4), the cycle math the ladder hangs off (node index = day of the
    /// PLAYER's local month, trimmed to that month's real length), and the catch-up rules that decide what a missed
    /// day costs: free for subscribers, a couple of rewarded ads for everyone else. Pure — no DB, no Redis.
    /// </summary>
    public class PassCatalogTests
    {
        private static readonly TimeZoneInfo Utc0 = TimeZoneInfo.Utc;

        private static DateTime Utc(int y, int m, int d, int h = 12) => new DateTime(y, m, d, h, 0, 0, DateTimeKind.Utc);
        private static DateTime Local(int y, int m, int d) => new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);

        private static PassNode Node(int index, RewardGrant[] free = null, RewardGrant[] golden = null, bool milestone = false)
            => new PassNode
            {
                Index = index,
                IsMilestone = milestone,
                Free = (free ?? new[] { RewardGrant.Currency("Chips", 100m) }).ToList(),
                Golden = (golden ?? Array.Empty<RewardGrant>()).ToList(),
            };

        private static PassProgram Program(params PassNode[] nodes) => new PassProgram
        {
            Key = "monthly",
            Title = "Monthly Pass",
            Cadence = PassCadence.Monthly,
            CatchUp = CatchUpPolicy.GoldenOrAds,
            AdsPerCatchUp = 2,
            MaxAdCatchUpsPerCycle = 5,
            GoldenProductIdApple = "khela.pass.golden.monthly",
            GoldenPriceUsd = 4.99m,
            Nodes = nodes.ToList(),
        };

        private static PassConfig Cfg(params PassProgram[] programs) => new PassConfig { Programs = programs.ToList() };

        private static PassProgram Ladder(int length)
            => Program(Enumerable.Range(1, length).Select(i => Node(i)).ToArray());

        private static PassCycle Cycle(PassProgram p, int y, int m, int d, TimeZoneInfo tz = null)
            => PassCatalog.CurrentCycle(p, Utc(y, m, d), tz ?? Utc0);

        // ---- the built-in program ----

        [Fact]
        public void Defaults_AreValid_AndRunAMonthlySelfRenewingProgram()
        {
            var cfg = PassCatalog.Defaults();
            Assert.Null(PassCatalog.Validate(cfg, ChestCatalog.Defaults()));

            var program = cfg.Default();
            Assert.Equal(PassCatalog.MonthlyKey, program.Key);
            Assert.Equal(PassCadence.Monthly, program.Cadence);
            Assert.Equal(CatchUpPolicy.GoldenOrAds, program.CatchUp);   // free players buy a missed day back with ads
            Assert.True(program.SellsGolden);
        }

        [Fact]
        public void Defaults_ClosingMilestoneIsReachableInFebruary()
        {
            var february = Cycle(PassCatalog.MonthlyProgram(), 2027, 2, 15);
            Assert.Equal(28, february.Length);
            Assert.True(february.Node(PassCatalog.GuaranteedDays).IsMilestone);
        }

        [Fact]
        public void Defaults_ChestRewardsExistInTheChestCatalog()
        {
            var chestLines = PassCatalog.DefaultLadder().SelectMany(n => n.Golden).Where(l => l.Kind == RewardKind.Chest).ToList();
            Assert.Equal(3, chestLines.Count);
            foreach (var line in chestLines)
            {
                Assert.True(Khela.Game.Services.Rewards.RewardIds.TryParseChest(line.Id, out var key, out var tier));
                Assert.NotNull(ChestCatalog.Defaults().Find(key, tier));
            }
        }

        [Fact]
        public void Defaults_NeverPayTheTradeableTokenOnEitherTrack()
        {
            var all = PassCatalog.DefaultLadder().SelectMany(n => n.Free.Concat(n.Golden));
            Assert.DoesNotContain(all, l => l.Kind == RewardKind.Currency && l.Id.Equals("Tokens", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Defaults_GoldenPaysMoreThanFree()
        {
            var free = PassCatalog.Totals(PassCatalog.DefaultLadder(), golden: false);
            var golden = PassCatalog.Totals(PassCatalog.DefaultLadder(), golden: true);
            Assert.True(golden["Chips"] > free["Chips"]);
            Assert.True(golden["Kash"] > free["Kash"]);
            Assert.True(golden["XP"] > free["XP"]);
        }

        // ---- cycle math: the month IS the ladder ----

        [Theory]
        [InlineData(2026, 9, 15, "2026-09", 30)]
        [InlineData(2027, 2, 1, "2027-02", 28)]
        [InlineData(2028, 2, 10, "2028-02", 29)]   // leap year
        [InlineData(2026, 12, 31, "2026-12", 31)]
        public void CurrentCycle_IsTheCalendarMonth_AndTrimsTheLadderToIt(int y, int m, int d, string key, int days)
        {
            var cycle = Cycle(PassCatalog.MonthlyProgram(), y, m, d);
            Assert.Equal(key, cycle.CycleKey);
            Assert.Equal(days, cycle.Days);
            Assert.Equal(days, cycle.Length);                     // a 31-node ladder stops where the month does
            Assert.Null(cycle.Node(days + 1));                    // nothing beyond the month is offered
        }

        [Fact]
        public void CurrentCycle_RollsOverOnTheFirst()
        {
            var program = PassCatalog.MonthlyProgram();
            var sept = PassCatalog.CurrentCycle(program, new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc), Utc0);
            var oct = PassCatalog.CurrentCycle(program, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Utc0);
            Assert.Equal("2026-09", sept.CycleKey);
            Assert.Equal("2026-10", oct.CycleKey);
            Assert.Equal(sept.EndUtc, oct.StartUtc);              // no gap, no overlap
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(30)]
        public void DayIndexAndMaxNode_TrackTheCalendar(int day)
        {
            var cycle = Cycle(PassCatalog.MonthlyProgram(), 2026, 9, day);
            Assert.Equal(day, cycle.DayIndex(Local(2026, 9, day)));
            Assert.Equal(day, cycle.MaxNode(Local(2026, 9, day)));   // never ahead of the calendar
        }

        [Fact]
        public void MaxNode_IsCappedByAShortLadder()
        {
            var cycle = Cycle(Ladder(10), 2026, 9, 25);
            Assert.Equal(25, cycle.DayIndex(Local(2026, 9, 25)));
            Assert.Equal(10, cycle.MaxNode(Local(2026, 9, 25)));   // a 10-node ladder finishes on day 10
        }

        [Fact]
        public void CurrentCycle_UsesAPerMonthOverrideWhenOneExists()
        {
            var program = PassCatalog.MonthlyProgram();
            program.CycleOverrides.Add(new PassCycleOverride
            {
                CycleKey = "2026-12",
                Title = "Festive Pass",
                Nodes = Enumerable.Range(1, 5).Select(i => Node(i)).ToList(),
            });

            Assert.Equal("Festive Pass", Cycle(program, 2026, 12, 5).Title);
            Assert.Equal(5, Cycle(program, 2026, 12, 5).Length);
            Assert.Equal(30, Cycle(program, 2026, 11, 5).Length);   // every other month keeps the recurring ladder
        }

        [Fact]
        public void CurrentCycle_IsNullWhenTheProgramIsOffOrTheFixedWindowHasPassed()
        {
            var off = PassCatalog.MonthlyProgram(); off.Enabled = false;
            Assert.Null(Cycle(off, 2026, 9, 15));

            var season = Ladder(60);
            season.Key = "season1";
            season.Cadence = PassCadence.Fixed;
            season.StartUtc = Utc(2026, 9, 1); season.EndUtc = Utc(2026, 10, 31);
            Assert.NotNull(Cycle(season, 2026, 10, 1));
            Assert.Null(Cycle(season, 2026, 11, 1));               // outside the window ⇒ pass off
        }

        [Fact]
        public void ASeasonPassIsJustAnotherProgram_LongerThanAMonth()
        {
            var season = Ladder(60);
            season.Key = "season1"; season.Title = "Season 1";
            season.Cadence = PassCadence.Fixed;
            season.StartUtc = Utc(2026, 9, 1); season.EndUtc = Utc(2026, 11, 1);

            var cfg = Cfg(PassCatalog.MonthlyProgram(), season);
            Assert.Null(PassCatalog.Validate(cfg, ChestCatalog.Defaults()));

            var cycle = Cycle(season, 2026, 9, 15);
            Assert.Equal("season1", cycle.CycleKey);               // fixed programs key their cycle by program
            Assert.Equal(60, cycle.Length);
            Assert.Equal(PassCatalog.MonthlyKey, cfg.Default().Key);   // the monthly pass is still the default
        }

        // ---- the player's own midnight, not UTC's ----

        [Fact]
        public void TheHostCanResolveIanaTimezones()
        {
            // An ENVIRONMENT guard, not a unit test: if the runtime is published in globalization-invariant mode (or
            // a container ships without tzdata), every player silently falls back to UTC and the daily pass rolls over
            // at breakfast in Dhaka — the exact thing this design exists to prevent. Fail loudly here instead.
            Assert.True(PassClock.IsKnown("Asia/Dhaka"), "No IANA tz data — check InvariantGlobalization / tzdata.");
            Assert.Equal(TimeSpan.FromHours(6), PassClock.Resolve("Asia/Dhaka").BaseUtcOffset);
        }

        [Fact]
        public void TheDayFlipsAtThePlayersLocalMidnight_NotUtcs()
        {
            var dhaka = PassClock.Resolve("Asia/Dhaka");                 // UTC+6, no DST
            var atUtc = new DateTime(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc);   // 02:00 on the 16th in Dhaka
            var dhakaCycle = PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), atUtc, dhaka);
            var utcCycle = PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), atUtc, Utc0);

            Assert.Equal(16, dhakaCycle.DayIndex(PassClock.LocalDate(atUtc, dhaka)));
            Assert.Equal(15, utcCycle.DayIndex(PassClock.LocalDate(atUtc, Utc0)));
        }

        [Fact]
        public void ANewCycleStartsAtLocalMidnight_SoDhakaIsNotStuckOnLastMonth()
        {
            var dhaka = PassClock.Resolve("Asia/Dhaka");
            var atUtc = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);   // already 1 Oct 02:00 in Dhaka
            Assert.Equal("2026-10", PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), atUtc, dhaka).CycleKey);
            Assert.Equal("2026-09", PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), atUtc, Utc0).CycleKey);
        }

        [Theory]
        [InlineData("Asia/Dhaka")]
        [InlineData("America/Santiago")]   // midnight DST transitions
        [InlineData("Asia/Tehran")]
        [InlineData("Pacific/Kiritimati")] // UTC+14, the extreme
        public void NextLocalMidnight_IsAlwaysARealInstantWithinADay(string tzId)
        {
            var tz = PassClock.Resolve(tzId);
            var now = new DateTime(2026, 9, 5, 13, 27, 0, DateTimeKind.Utc);
            for (int i = 0; i < 400; i++)   // walk a year of days, including every DST transition in that zone
            {
                var at = now.AddDays(i);
                var next = PassClock.NextLocalMidnightUtc(at, tz);
                Assert.True(next > at, $"{tzId}: next reset must be in the future");
                Assert.True(next <= at.AddHours(25), $"{tzId}: next reset must be within a day");
            }
        }

        [Fact]
        public void AnUnknownOrHostileTimezoneFallsBackToUtc_NeverThrows()
        {
            Assert.Equal(TimeZoneInfo.Utc, PassClock.Resolve(null));
            Assert.Equal(TimeZoneInfo.Utc, PassClock.Resolve(""));
            Assert.Equal(TimeZoneInfo.Utc, PassClock.Resolve("Mars/Olympus_Mons"));
            Assert.Equal(TimeZoneInfo.Utc, PassClock.Resolve("'; DROP TABLE PlayerPassClaims;--"));
            Assert.False(PassClock.IsKnown("Mars/Olympus_Mons"));
        }

        // ---- catch-up: what a missed day costs ----

        [Fact]
        public void TodaysNodeIsAlwaysFree()
        {
            var cycle = Cycle(PassCatalog.MonthlyProgram(), 2026, 9, 20);
            var a = PassCatalog.Availability(cycle, Local(2026, 9, 20), new HashSet<int>(), isGolden: false);
            Assert.Contains(20, a.Claimable);
        }

        [Fact]
        public void GoldenOrAds_SubscriberBackfillsFree_EveryoneElsePaysInAds()
        {
            var cycle = Cycle(PassCatalog.MonthlyProgram(), 2026, 9, 20);
            var today = Local(2026, 9, 20);
            var claimed = new HashSet<int> { 1, 2, 3 };

            var golden = PassCatalog.Availability(cycle, today, claimed, isGolden: true);
            Assert.Equal(Enumerable.Range(4, 17), golden.Claimable);   // 4..20, free
            Assert.Empty(golden.AdUnlockable);

            var free = PassCatalog.Availability(cycle, today, claimed, isGolden: false);
            Assert.Equal(new[] { 20 }, free.Claimable);                 // only today at no cost
            Assert.Equal(2, free.AdsPerUnlock);
            Assert.Equal(5, free.AdUnlocksLeft);                        // how MANY days ads can buy back this cycle
            Assert.Equal(Enumerable.Range(4, 16), free.AdUnlockable);   // …but the player picks WHICH: any missed day
            Assert.Equal(Enumerable.Range(4, 16), free.GoldenLocked);   // the subscription hands over all 16 free
        }

        [Fact]
        public void AdCatchUpsAreCappedPerCycle_AndSpentOnesCountAgainstIt()
        {
            var cycle = Cycle(PassCatalog.MonthlyProgram(), 2026, 9, 20);
            var today = Local(2026, 9, 20);

            var fresh = PassCatalog.Availability(cycle, today, new HashSet<int>(), isGolden: false, adCatchUpsUsed: 0);
            Assert.Equal(5, fresh.AdUnlocksLeft);
            Assert.Equal(19, fresh.AdUnlockable.Count);                 // any of the 19 missed days is a candidate

            var partly = PassCatalog.Availability(cycle, today, new HashSet<int>(), isGolden: false, adCatchUpsUsed: 4);
            Assert.Equal(1, partly.AdUnlocksLeft);                      // one buy-back left…
            Assert.Equal(19, partly.AdUnlockable.Count);                // …still their choice which day to spend it on

            var spent = PassCatalog.Availability(cycle, today, new HashSet<int>(), isGolden: false, adCatchUpsUsed: 5);
            Assert.Equal(0, spent.AdUnlocksLeft);
            Assert.Empty(spent.AdUnlockable);
            Assert.Equal(new[] { 20 }, spent.Claimable);               // today still free
        }

        [Fact]
        public void AdCatchUpCanBeTurnedOffWithoutChangingThePolicy()
        {
            var program = PassCatalog.MonthlyProgram();
            program.MaxAdCatchUpsPerCycle = 0;
            var a = PassCatalog.Availability(Cycle(program, 2026, 9, 20), Local(2026, 9, 20), new HashSet<int>(), isGolden: false);
            Assert.Empty(a.AdUnlockable);
            Assert.Equal(19, a.GoldenLocked.Count);                    // days 1..19 now need the subscription
        }

        [Fact]
        public void CatchUpNone_NobodyBackfills_AndAllLetsEveryoneBackfillFree()
        {
            var program = PassCatalog.MonthlyProgram();
            var today = Local(2026, 9, 20);

            program.CatchUp = CatchUpPolicy.None;
            var none = PassCatalog.Availability(Cycle(program, 2026, 9, 20), today, new HashSet<int>(), isGolden: true);
            Assert.Equal(new[] { 20 }, none.Claimable);
            Assert.Empty(none.AdUnlockable);

            program.CatchUp = CatchUpPolicy.All;
            var all = PassCatalog.Availability(Cycle(program, 2026, 9, 20), today, new HashSet<int>(), isGolden: false);
            Assert.Equal(Enumerable.Range(1, 20), all.Claimable);
        }

        [Fact]
        public void AlreadyClaimedNodesAreNeverOfferedAgain()
        {
            var cycle = Cycle(PassCatalog.MonthlyProgram(), 2026, 9, 5);
            var today = Local(2026, 9, 5);
            Assert.Empty(PassCatalog.Availability(cycle, today, new HashSet<int> { 5 }, isGolden: false).Claimable);

            var all = PassCatalog.Availability(cycle, today, new HashSet<int> { 1, 2, 3, 4, 5 }, isGolden: true);
            Assert.Empty(all.Claimable);
            Assert.Empty(all.AdUnlockable);
            Assert.Empty(all.GoldenLocked);
        }

        [Fact]
        public void NothingFromLastCycleIsClaimableAfterRollover()
        {
            var october = Cycle(PassCatalog.MonthlyProgram(), 2026, 10, 1);
            var a = PassCatalog.Availability(october, Local(2026, 10, 1), new HashSet<int>(), isGolden: true);
            Assert.Equal(new[] { 1 }, a.Claimable);   // September's claims live under another cycle key entirely
        }

        // ---- the legal guardrail ----

        [Fact]
        public void Validate_RefusesTheTradeableTokenOnEitherTrack()
        {
            Assert.Contains("tradeable token",
                PassCatalog.Validate(Cfg(Program(Node(1, free: new[] { RewardGrant.Currency("Tokens", 5m) })))));
            Assert.Contains("tradeable token",
                PassCatalog.Validate(Cfg(Program(Node(1, golden: new[] { RewardGrant.Currency("Tokens", 5m) })))));
        }

        [Theory]
        [InlineData("3")]          // the token's int — a numeric id must never resolve to a currency
        [InlineData("Bitcoin")]
        [InlineData("")]
        public void Validate_RefusesAnUnknownCurrencyName(string id)
            => Assert.NotNull(PassCatalog.Validate(Cfg(Program(Node(1, free: new[] { RewardGrant.Currency(id, 100m) })))));

        // ---- structure the claim path depends on ----

        [Fact]
        public void Validate_RequiresAContiguousLadderFromOne()
        {
            Assert.Contains("no gaps", PassCatalog.Validate(Cfg(Program(Node(1), Node(3)))));   // gap
            Assert.Contains("no gaps", PassCatalog.Validate(Cfg(Program(Node(0), Node(1)))));   // 0-based
            Assert.Contains("no gaps", PassCatalog.Validate(Cfg(Program(Node(1), Node(1)))));   // duplicate
            Assert.Null(PassCatalog.Validate(Cfg(Program(Node(1), Node(2), Node(3)))));
        }

        [Fact]
        public void Validate_RefusesAMonthlyLadderLongerThanTheLongestMonth()
        {
            Assert.Null(PassCatalog.Validate(Cfg(Ladder(PassCatalog.MaxMonthlyNodes))));
            Assert.Contains("exceeds", PassCatalog.Validate(Cfg(Ladder(PassCatalog.MaxMonthlyNodes + 1))));
        }

        [Fact]
        public void Validate_RefusesAMilestoneFebruaryCanNeverReach()
        {
            var program = Ladder(31);
            program.Nodes[29].IsMilestone = true;   // day 30
            Assert.Contains("can't be reached in February", PassCatalog.Validate(Cfg(program)));

            program.Nodes[29].IsMilestone = false;
            program.Nodes[27].IsMilestone = true;   // day 28
            Assert.Null(PassCatalog.Validate(Cfg(program)));
        }

        [Fact]
        public void Validate_RefusesDuplicateProgramKeysAndOverlongKeys()
        {
            Assert.Contains("Duplicate program key", PassCatalog.Validate(Cfg(Ladder(3), Ladder(3))));

            var p = Ladder(3);
            p.Key = new string('x', PassCatalog.MaxKeyLength + 1);
            Assert.Contains("longer than", PassCatalog.Validate(Cfg(p)));
        }

        [Fact]
        public void Validate_RefusesEmptyNodesAndEmptyLadders()
        {
            Assert.Contains("Free or Golden", PassCatalog.Validate(Cfg(Program(new PassNode { Index = 1 }))));
            Assert.Contains("at least one node", PassCatalog.Validate(Cfg(Program())));
        }

        [Fact]
        public void Validate_RefusesNonPositiveAmounts()
            => Assert.Contains("greater than 0",
                PassCatalog.Validate(Cfg(Program(Node(1, free: new[] { RewardGrant.Currency("Chips", 0m) })))));

        [Fact]
        public void Validate_ChecksTheAdCatchUpSettings()
        {
            var p = Ladder(5);
            p.AdsPerCatchUp = PassCatalog.MaxAdsPerCatchUp + 1;
            Assert.Contains("ads per catch-up", PassCatalog.Validate(Cfg(p)));

            p.AdsPerCatchUp = 0;                       // "free catch-up for everyone" is not what GoldenOrAds means
            Assert.Contains("costs 0 ads", PassCatalog.Validate(Cfg(p)));

            p.MaxAdCatchUpsPerCycle = 0;               // …unless ad catch-up is switched off outright
            Assert.Null(PassCatalog.Validate(Cfg(p)));
        }

        [Fact]
        public void Validate_RefusesAFixedProgramWithoutAValidWindow()
        {
            var p = Ladder(5);
            p.Cadence = PassCadence.Fixed;
            Assert.Contains("needs a start and an end", PassCatalog.Validate(Cfg(p)));

            p.StartUtc = Utc(2026, 9, 1); p.EndUtc = Utc(2026, 9, 1);
            Assert.Contains("ends before it starts", PassCatalog.Validate(Cfg(p)));
        }

        [Fact]
        public void Validate_ChecksCycleOverrides()
        {
            var p = Ladder(5);
            p.CycleOverrides.Add(new PassCycleOverride { CycleKey = "December", Nodes = new List<PassNode> { Node(1) } });
            Assert.Contains("must look like", PassCatalog.Validate(Cfg(p)));

            p.CycleOverrides[0].CycleKey = "2026-12";
            Assert.Null(PassCatalog.Validate(Cfg(p)));

            p.CycleOverrides[0].Nodes = new List<PassNode> { Node(1), Node(3) };   // same ladder rules apply
            Assert.Contains("no gaps", PassCatalog.Validate(Cfg(p)));
        }

        // ---- rewards that can't actually be paid ----

        [Fact]
        public void Validate_ChecksChestIdsAgainstTheChestCatalog()
        {
            var chests = ChestCatalog.Defaults();
            Assert.Null(PassCatalog.Validate(Cfg(Program(Node(1, free: new[] { RewardGrant.Chest("CK_Chest:Rare") }))), chests));
            Assert.Contains("isn't in the chest catalog",
                PassCatalog.Validate(Cfg(Program(Node(1, free: new[] { RewardGrant.Chest("No_Such_Chest:Rare") }))), chests));
            Assert.Contains("not a chest id",
                PassCatalog.Validate(Cfg(Program(Node(1, free: new[] { RewardGrant.Chest("CK_Chest") }))), chests));
        }

        [Fact]
        public void Validate_RefusesAKindThisBuildCannotPayOut()
        {
            var payable = new HashSet<RewardKind> { RewardKind.Currency, RewardKind.Xp, RewardKind.Chest };
            var cfg = Cfg(Program(Node(1, free: new[] { new RewardGrant { Kind = RewardKind.Item, Id = "lottery_ticket", Amount = 1m } })));

            Assert.Contains("can't be paid out by this build", PassCatalog.Validate(cfg, null, payable));
            Assert.Null(PassCatalog.Validate(cfg));   // no payable-set supplied → the shape itself is fine
        }

        [Fact]
        public void Validate_RefusesGoldenRewardsWithNoWayToSubscribe()
        {
            var p = Program(Node(1, golden: new[] { RewardGrant.Currency("Chips", 100m) }));
            p.GoldenProductIdApple = null; p.GoldenProductIdGoogle = null;
            Assert.Contains("no store product id", PassCatalog.Validate(Cfg(p)));

            p.GoldenProductIdGoogle = "khela.pass.golden.monthly";
            Assert.Null(PassCatalog.Validate(Cfg(p)));
        }

        [Fact]
        public void Validate_AllowsAFreeOnlyProgramWithNoStoreProduct()
        {
            var p = Program(Node(1));
            p.GoldenProductIdApple = null; p.GoldenProductIdGoogle = null;
            Assert.Null(PassCatalog.Validate(Cfg(p)));   // the free track must be shippable before IAP exists
        }

        // ---- effective-config plumbing ----

        [Fact]
        public void Default_IsNullWhenThePassIsDisabled()
        {
            var cfg = PassCatalog.Defaults();
            cfg.Enabled = false;
            Assert.Null(cfg.Default());
        }

        [Fact]
        public void TryParse_RoundTripsAConfigAndRejectsGarbage()
        {
            var json = PassCatalog.ToJson(PassCatalog.Defaults());
            var parsed = PassCatalog.TryParse(json);

            Assert.NotNull(parsed);
            Assert.Null(PassCatalog.Validate(parsed, ChestCatalog.Defaults()));
            Assert.Equal(PassCatalog.DefaultLadderLength, parsed.Default().Nodes.Count);
            Assert.Equal(2, parsed.Default().AdsPerCatchUp);
            Assert.Contains("\"Currency\"", json);        // enums as names, so the admin JSON stays readable
            Assert.Contains("\"GoldenOrAds\"", json);

            Assert.Null(PassCatalog.TryParse("{ not json"));
            Assert.Null(PassCatalog.TryParse(""));
        }
    }
}
