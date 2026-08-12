using System;
using System.Collections.Generic;
using System.Linq;
using Khela.Game.Managers;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// How a bet tier grows and shrinks. A tier is however many tables its players need — thirty of them if ninety
    /// people want that stake — and it has to get there on its own and come back down afterwards, without ever
    /// dropping below the floors that make the lobby feel alive.
    /// </summary>
    public class LobbyPlanTests
    {
        private const int MinTables = 3, MinJoinable = 3, MinEmpty = 1, Seats = 3;
        private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

        private static TableCapacity T(string id, int occupied, int emptyForMinutes = 0) => new()
        {
            TableId = id,
            Occupied = occupied,
            Capacity = Seats,
            EmptySince = occupied == 0 ? Now.AddMinutes(-emptyForMinutes) : null,
        };

        private static int Create(IEnumerable<TableCapacity> tier)
            => LobbyPlan.TablesToCreate(tier, MinTables, MinJoinable, MinEmpty);

        private static IReadOnlyList<string> Remove(IEnumerable<TableCapacity> tier)
            => LobbyPlan.TablesToRemove(tier, MinTables, MinJoinable, MinEmpty, Grace, Now);

        [Fact]
        public void AnEmptyTierIsStockedToItsFloor()
        {
            Assert.Equal(3, Create(new List<TableCapacity>()));
        }

        [Fact]
        public void AHealthyTierNeedsNothing()
        {
            // Three tables, one full, one part-full, one empty: 3 total, 2 joinable... still one joinable short.
            Assert.Equal(1, Create(new[] { T("a", 3), T("b", 1), T("c", 0) }));

            // Add another and every floor is met: 4 total, 3 joinable (b, c, d), 1 empty (c, d -> 2).
            Assert.Equal(0, Create(new[] { T("a", 3), T("b", 1), T("c", 0), T("d", 0) }));
        }

        [Fact]
        public void APartlyOccupiedTableStillCountsAsJoinable()
        {
            // The user's point: the spare tables are allowed to have people at them — "joinable" is a free SEAT,
            // not an empty table. Three part-full tables plus one empty satisfies the tier.
            var tier = new[] { T("a", 2), T("b", 2), T("c", 2), T("d", 0) };
            Assert.Equal(0, Create(tier));
        }

        [Fact]
        public void ThereIsAlwaysSomewhereToPlayAlone()
        {
            // Three joinable tables, but every one of them has players. Someone who wants a table to themselves
            // has nowhere to go, so the tier owes one more.
            var tier = new[] { T("a", 1), T("b", 1), T("c", 1) };
            Assert.Equal(1, Create(tier));

            Assert.Equal(0, Create(tier.Append(T("d", 0))));
        }

        [Fact]
        public void AFillingTierKeepsOpeningTables()
        {
            // Everything full: no joinable, no empty. Needs three joinable and one of them empty — three covers both,
            // because a new table is empty AND joinable.
            var full = Enumerable.Range(0, 10).Select(i => T($"t{i}", 3)).ToList();
            Assert.Equal(3, Create(full));
        }

        [Fact]
        public void NinetyPlayersEndUpWithThirtyBusyTablesPlusSpares()
        {
            // The scenario as stated: 90 players at 3 seats. Simulate arrivals filling tables and re-balancing.
            var tier = new List<TableCapacity>();
            int seated = 0, nextId = 0;

            for (int i = 0; i < 200 && seated < 90; i++)
            {
                foreach (var _ in Enumerable.Range(0, Create(tier)))
                    tier.Add(T($"t{nextId++}", 0));

                // One player sits at the fullest table that still has room (what the display order encourages).
                var target = tier.Where(t => t.IsJoinable).OrderByDescending(t => t.Occupied).First();
                tier[tier.IndexOf(target)] = T(target.TableId, target.Occupied + 1);
                seated++;
            }

            foreach (var _ in Enumerable.Range(0, Create(tier))) tier.Add(T($"t{nextId++}", 0));

            Assert.Equal(90, tier.Sum(t => t.Occupied));
            Assert.Equal(30, tier.Count(t => t.Occupied == Seats));      // 30 full tables, exactly as expected
            Assert.True(tier.Count(t => t.IsJoinable) >= MinJoinable);
            Assert.True(tier.Count(t => t.IsEmpty) >= MinEmpty);
            Assert.Equal(0, Create(tier));                                // settled — no runaway creation
        }

        [Fact]
        public void ARushDoesNotOverShoot()
        {
            // Re-running the balancer without anyone joining must not keep adding tables.
            var tier = new List<TableCapacity>();
            for (int pass = 0; pass < 5; pass++)
                foreach (var _ in Enumerable.Range(0, Create(tier))) tier.Add(T($"x{tier.Count}", 0));

            Assert.Equal(3, tier.Count);
        }

        // ---- shrinking ------------------------------------------------------------------------------------------

        [Fact]
        public void ATierNeverShrinksBelowItsFloor()
        {
            var tier = new[] { T("a", 0, 60), T("b", 0, 60), T("c", 0, 60) };
            Assert.Empty(Remove(tier));
        }

        [Fact]
        public void SurplusEmptyTablesAreGivenUpOldestFirst()
        {
            // Six tables, all long empty. Floor is 3, so exactly three may go — the three that have been empty longest.
            var tier = new[] { T("a", 0, 10), T("b", 0, 30), T("c", 0, 20), T("d", 0, 5), T("e", 0, 40), T("f", 0, 1) };
            var doomed = Remove(tier);

            Assert.Equal(3, doomed.Count);
            Assert.Equal(new[] { "e", "b", "c" }, doomed.ToArray());   // 40, 30, 20 minutes empty
        }

        [Fact]
        public void ATableIsGivenAGraceBeforeItIsRemoved()
        {
            // Someone standing up must not delete the table out from under a browsing player, or make it flicker
            // when they sit back down.
            var tier = new[] { T("a", 0, 0), T("b", 0, 0), T("c", 0, 0), T("d", 0, 0), T("e", 0, 0) };
            Assert.Empty(Remove(tier));

            var aged = tier.Select(t => T(t.TableId, 0, 10)).ToArray();
            Assert.Equal(2, Remove(aged).Count);
        }

        [Fact]
        public void OccupiedTablesAreNeverRemoved()
        {
            var tier = new[] { T("a", 1), T("b", 2), T("c", 3), T("d", 0, 60), T("e", 0, 60), T("f", 0, 60) };
            var doomed = Remove(tier);

            Assert.DoesNotContain("a", doomed);
            Assert.DoesNotContain("b", doomed);
            Assert.DoesNotContain("c", doomed);
            // a and b are joinable, so joinable stays >= 3 while two of the empties go; one empty must remain.
            Assert.Equal(2, doomed.Count);
        }

        [Fact]
        public void ShrinkingLeavesTheTierBalancedAgain()
        {
            // After a shrink the tier must not immediately want to create tables again — otherwise it oscillates.
            var tier = new List<TableCapacity>(Enumerable.Range(0, 12).Select(i => T($"t{i}", 0, 30)));
            var doomed = Remove(tier).ToHashSet();
            var after = tier.Where(t => !doomed.Contains(t.TableId)).ToList();

            Assert.Equal(0, Create(after));
        }

        // ---- paging ---------------------------------------------------------------------------------------------
        // A busy tier is dozens of tables. The browser gets a composed page, not the first N by occupancy.

        private const int Page = 15, FullQuota = 3, EmptyQuota = 1;

        private static IReadOnlyList<TableCapacity> Paged(IEnumerable<TableCapacity> tier)
            => LobbyPlan.Page(tier, t => t, Page, FullQuota, EmptyQuota);

        /// <summary>A realistic busy tier: lots of full tables, a spread of part-full ones, a few empties.</summary>
        private static List<TableCapacity> BusyTier()
        {
            var list = new List<TableCapacity>();
            for (int i = 0; i < 20; i++) list.Add(T($"full{i}", 3));
            for (int i = 0; i < 8; i++) list.Add(T($"two{i}", 2));
            for (int i = 0; i < 6; i++) list.Add(T($"one{i}", 1));
            for (int i = 0; i < 4; i++) list.Add(T($"empty{i}", 0, 5));
            return list;
        }

        [Fact]
        public void ASmallTierIsSentWhole()
        {
            var tier = new[] { T("a", 3), T("b", 1), T("c", 0) };
            Assert.Equal(3, Paged(tier).Count);
        }

        [Fact]
        public void ABusyTierIsCappedAtThePageSize()
        {
            Assert.Equal(Page, Paged(BusyTier()).Count);
        }

        [Fact]
        public void ThePageAlwaysKeepsAnEmptyTableToPlayAloneAt()
        {
            // The whole point: "busiest 15" would discard every empty table exactly when the tier is popular.
            var page = Paged(BusyTier());
            Assert.Contains(page, t => t.IsEmpty);
        }

        [Fact]
        public void ThePageShowsAFewFullTablesButIsMostlyJoinableOnes()
        {
            var page = Paged(BusyTier());
            int fullShown = page.Count(t => !t.IsJoinable);
            int partShown = page.Count(t => t.IsJoinable && !t.IsEmpty);

            Assert.Equal(FullQuota, fullShown);            // a few, so the room looks alive
            Assert.True(partShown > fullShown, $"expected mostly joinable tables, got {partShown} vs {fullShown} full");
        }

        [Fact]
        public void ThePageIsOrderedFullestFirstWithTheEmptyLast()
        {
            var page = Paged(BusyTier());
            Assert.True(page[0].Occupied >= page[1].Occupied);
            Assert.True(page[^1].IsEmpty, "the last card must be the free table");
        }

        [Fact]
        public void ATierWithNothingButFullTablesStillFillsThePage()
        {
            // No partials and no empties to reserve — the page should not come back nearly blank.
            var tier = Enumerable.Range(0, 30).Select(i => T($"f{i}", 3)).ToList();
            Assert.Equal(Page, Paged(tier).Count);
        }

        [Fact]
        public void ATierWithNoEmptyTablesIsStillPagedSanely()
        {
            var tier = Enumerable.Range(0, 25).Select(i => T($"p{i}", i % 3 == 0 ? 3 : 2)).ToList();
            var page = Paged(tier);
            Assert.Equal(Page, page.Count);
            Assert.DoesNotContain(page, t => t.IsEmpty);   // nothing invented that isn't there
        }

        // ---- display order --------------------------------------------------------------------------------------

        [Fact]
        public void TheEmptyTableSortsLastSoItIsTheOneYouPlayAloneAt()
        {
            var tier = new[] { T("empty", 0), T("one", 1), T("full", 3), T("two", 2) };
            var shown = LobbyPlan.ForDisplay(tier, t => t).Select(t => t.TableId).ToArray();

            Assert.Equal(new[] { "full", "two", "one", "empty" }, shown);
        }

        [Fact]
        public void AllEmptyTierKeepsAStableOrder()
        {
            var tier = new[] { T("c", 0), T("a", 0), T("b", 0) };
            Assert.Equal(new[] { "a", "b", "c" }, LobbyPlan.ForDisplay(tier, t => t).Select(t => t.TableId).ToArray());
        }
    }
}
