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
    /// Locks the monthly pass catalog (docs/PASS_SPEC.md §3/§5.1) — the legal guardrail that no reward may ever be the
    /// tradeable token (CLAUDE.md NON-NEGOTIABLE #2/#4), the cycle math the whole ladder hangs off (node index = day of
    /// the month, trimmed to the month's real length), and the catch-up policy that decides what a subscription
    /// actually BUYS. Pure — no DB, no Redis.
    /// </summary>
    public class PassCatalogTests
    {
        private static DateTime Utc(int y, int m, int d) => new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Utc);

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
            CatchUp = CatchUpPolicy.GoldenOnly,
            GoldenProductIdApple = "khela.pass.golden.monthly",
            GoldenPriceUsd = 4.99m,
            Nodes = nodes.ToList(),
        };

        private static PassConfig Cfg(params PassProgram[] programs) => new PassConfig { Programs = programs.ToList() };

        private static PassProgram Ladder(int length)
            => Program(Enumerable.Range(1, length).Select(i => Node(i)).ToArray());

        // ---- the built-in program ----

        [Fact]
        public void Defaults_AreValid_AndRunAMonthlySelfRenewingProgram()
        {
            var cfg = PassCatalog.Defaults();
            Assert.Null(PassCatalog.Validate(cfg, ChestCatalog.Defaults()));

            var program = cfg.Default();
            Assert.Equal(PassCatalog.MonthlyKey, program.Key);
            Assert.Equal(PassCadence.Monthly, program.Cadence);
            Assert.Equal(CatchUpPolicy.GoldenOnly, program.CatchUp);   // catch-up is what the subscription buys
            Assert.True(program.SellsGolden);
        }

        [Fact]
        public void Defaults_ClosingMilestoneIsReachableInFebruary()
        {
            var february = PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), Utc(2027, 2, 15));
            Assert.Equal(28, february.Length);
            Assert.True(february.Node(PassCatalog.GuaranteedDays).IsMilestone);   // the big node still exists in Feb
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
            var cycle = PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), Utc(y, m, d));
            Assert.Equal(key, cycle.CycleKey);
            Assert.Equal(days, cycle.Days);
            Assert.Equal(days, cycle.Length);                     // a 31-node ladder stops where the month does
            Assert.Equal(new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc), cycle.StartUtc);
            Assert.Null(cycle.Node(days + 1));                    // nothing beyond the month is offered
        }

        [Fact]
        public void CurrentCycle_RollsOverOnTheFirst()
        {
            var program = PassCatalog.MonthlyProgram();
            var sept = PassCatalog.CurrentCycle(program, new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc));
            var oct = PassCatalog.CurrentCycle(program, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("2026-09", sept.CycleKey);
            Assert.Equal("2026-10", oct.CycleKey);
            Assert.Equal(sept.EndUtc, oct.StartUtc);              // no gap, no overlap
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(7, 7)]
        [InlineData(30, 30)]
        public void DayIndexAndMaxNode_TrackTheCalendar(int day, int expected)
        {
            var cycle = PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), Utc(2026, 9, day));
            Assert.Equal(expected, cycle.DayIndex(Utc(2026, 9, day)));
            Assert.Equal(expected, cycle.MaxNode(Utc(2026, 9, day)));   // never ahead of the calendar
        }

        [Fact]
        public void MaxNode_IsCappedByAShortLadder()
        {
            var cycle = PassCatalog.CurrentCycle(Ladder(10), Utc(2026, 9, 25));
            Assert.Equal(25, cycle.DayIndex(Utc(2026, 9, 25)));
            Assert.Equal(10, cycle.MaxNode(Utc(2026, 9, 25)));    // a 10-node ladder finishes on day 10
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

            var december = PassCatalog.CurrentCycle(program, Utc(2026, 12, 5));
            var november = PassCatalog.CurrentCycle(program, Utc(2026, 11, 5));
            Assert.Equal("Festive Pass", december.Title);
            Assert.Equal(5, december.Length);
            Assert.Equal(30, november.Length);                    // every other month keeps the recurring ladder
        }

        [Fact]
        public void CurrentCycle_IsNullWhenTheProgramIsOffOrTheFixedWindowHasPassed()
        {
            var off = PassCatalog.MonthlyProgram(); off.Enabled = false;
            Assert.Null(PassCatalog.CurrentCycle(off, Utc(2026, 9, 15)));

            var season = Ladder(60);
            season.Key = "season1";
            season.Cadence = PassCadence.Fixed;
            season.StartUtc = Utc(2026, 9, 1); season.EndUtc = Utc(2026, 10, 31);
            Assert.NotNull(PassCatalog.CurrentCycle(season, Utc(2026, 10, 1)));
            Assert.Null(PassCatalog.CurrentCycle(season, Utc(2026, 11, 1)));   // outside the window ⇒ pass off
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

            var cycle = PassCatalog.CurrentCycle(season, Utc(2026, 9, 15));
            Assert.Equal("season1", cycle.CycleKey);              // fixed programs key their cycle by program
            Assert.Equal(60, cycle.Length);
            Assert.Equal(PassCatalog.MonthlyKey, cfg.Default().Key);   // the monthly pass is still the default
        }

        // ---- catch-up: what the subscription buys ----

        [Fact]
        public void GoldenOnly_FreePlayerGetsTodayOnly_GoldenPlayerBackfills()
        {
            var cycle = PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), Utc(2026, 9, 20));
            var now = Utc(2026, 9, 20);
            var claimed = new HashSet<int> { 1, 2, 3 };

            var free = PassCatalog.ClaimableNodes(cycle, now, claimed, isGolden: false);
            Assert.Equal(new[] { 20 }, free);                     // missed days are gone for a free player

            var golden = PassCatalog.ClaimableNodes(cycle, now, claimed, isGolden: true);
            Assert.Equal(Enumerable.Range(4, 17), golden);        // 4..20 — the missed days come back
        }

        [Fact]
        public void CatchUpNone_NobodyBackfills_AndAllLetsEveryoneBackfill()
        {
            var program = PassCatalog.MonthlyProgram();
            var now = Utc(2026, 9, 20);

            program.CatchUp = CatchUpPolicy.None;
            var none = PassCatalog.CurrentCycle(program, now);
            Assert.Equal(new[] { 20 }, PassCatalog.ClaimableNodes(none, now, new HashSet<int>(), isGolden: true));

            program.CatchUp = CatchUpPolicy.All;
            var all = PassCatalog.CurrentCycle(program, now);
            Assert.Equal(Enumerable.Range(1, 20), PassCatalog.ClaimableNodes(all, now, new HashSet<int>(), isGolden: false));
        }

        [Fact]
        public void AlreadyClaimedNodesAreNeverOfferedAgain()
        {
            var cycle = PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), Utc(2026, 9, 5));
            var now = Utc(2026, 9, 5);
            Assert.Empty(PassCatalog.ClaimableNodes(cycle, now, new HashSet<int> { 5 }, isGolden: false));
            Assert.Empty(PassCatalog.ClaimableNodes(cycle, now, new HashSet<int> { 1, 2, 3, 4, 5 }, isGolden: true));
        }

        [Fact]
        public void LockedByGolden_IsTheSubscribeCtaNumber()
        {
            var cycle = PassCatalog.CurrentCycle(PassCatalog.MonthlyProgram(), Utc(2026, 9, 20));
            var now = Utc(2026, 9, 20);
            var claimed = new HashSet<int> { 1, 2, 3 };

            Assert.Equal(16, PassCatalog.LockedByGolden(cycle, now, claimed, isGolden: false));  // days 4..19
            Assert.Equal(0, PassCatalog.LockedByGolden(cycle, now, claimed, isGolden: true));    // already subscribed
        }

        [Fact]
        public void NothingFromLastCycleIsClaimableAfterRollover()
        {
            var program = PassCatalog.MonthlyProgram();
            var october = PassCatalog.CurrentCycle(program, Utc(2026, 10, 1));
            // September's claims belong to another cycle key entirely, so October starts empty at node 1.
            Assert.Equal(new[] { 1 }, PassCatalog.ClaimableNodes(october, Utc(2026, 10, 1), new HashSet<int>(), isGolden: true));
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
            Assert.Contains("\"Currency\"", json);        // enums as names, so the admin JSON stays readable
            Assert.Contains("\"GoldenOnly\"", json);

            Assert.Null(PassCatalog.TryParse("{ not json"));
            Assert.Null(PassCatalog.TryParse(""));
        }
    }
}
