using System;
using System.Collections.Generic;
using System.Linq;

namespace Khela.Game.Managers
{
    /// <summary>
    /// A stake bracket the lobby offers. Tables are created per tier on demand — a tier is a category of table,
    /// not a single table.
    /// </summary>
    public sealed class BetTier
    {
        public decimal MinBet { get; set; }
        public decimal MaxBet { get; set; }

        /// <summary>
        /// Stable key for matching a table to its tier and for the client's filter. Formatted to four decimals
        /// rather than rounded to whole chips: this is used as a dictionary identity, and a rounding format would
        /// make two genuinely different brackets collide.
        /// </summary>
        public string Key => $"{MinBet:0.####}-{MaxBet:0.####}";

        public bool Matches(decimal min, decimal max) => MinBet == min && MaxBet == max;

        public override string ToString() => Key;
    }

    /// <summary>One table, reduced to just what the lobby balancer needs to reason about.</summary>
    public sealed class TableCapacity
    {
        public string TableId { get; init; }

        /// <summary>Seats currently taken.</summary>
        public int Occupied { get; init; }

        /// <summary>Seats a player can actually sit in (the PLAYABLE cap, not the raw seat count).</summary>
        public int Capacity { get; init; }

        /// <summary>When this table last became empty, or null if someone is sitting at it.</summary>
        public DateTimeOffset? EmptySince { get; init; }

        public bool IsEmpty => Occupied <= 0;

        /// <summary>Has a free seat — note a joinable table may already have players at it.</summary>
        public bool IsJoinable => Occupied < Capacity;
    }

    /// <summary>
    /// Keeps each bet tier stocked with tables.
    ///
    /// A bet tier is not one table — it is however many tables the people playing that stake need. Ninety players
    /// at three seats a table is thirty tables, and the lobby has to grow to that on its own and shrink back
    /// afterwards. This decides both, as a pure function of what the tier currently looks like, so the rules can be
    /// tested without Redis, a clock, or a running game.
    ///
    /// The three floors it maintains per tier:
    ///   • a minimum number of tables, so a dead tier still looks like a casino rather than an empty room;
    ///   • a minimum number of JOINABLE tables — ones with a free seat, which may already have players at them,
    ///     so arriving players land together instead of each opening their own table;
    ///   • at least one COMPLETELY EMPTY table, so a player who wants to play alone always has somewhere to go.
    /// </summary>
    public static class LobbyPlan
    {
        /// <summary>
        /// Clean a configured tier list into something safe to key a dictionary by: drop entries that make no sense,
        /// collapse duplicates, and put them in stake order.
        ///
        /// The de-duplication is the important part. Tiers are grouped by <see cref="BetTier.Key"/> downstream, and
        /// a repeated bracket — trivially easy to produce by hand-editing the admin override — would otherwise throw
        /// out of the lobby endpoint and take the whole table browser down until someone edited Redis by hand.
        /// </summary>
        public static List<BetTier> Sanitize(IEnumerable<BetTier> tiers)
            => (tiers ?? Enumerable.Empty<BetTier>())
                .Where(t => t != null && t.MinBet > 0 && t.MaxBet >= t.MinBet)
                .GroupBy(t => t.Key, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(t => t.MinBet).ThenBy(t => t.MaxBet)
                .ToList();

        /// <summary>
        /// How many new tables this tier needs right now. Zero when every floor is already met.
        ///
        /// One new table is empty and has every seat free, so it counts towards all three floors at once — which is
        /// why this is the largest single deficit rather than their sum.
        /// </summary>
        public static int TablesToCreate(IEnumerable<TableCapacity> tier, int minTables, int minJoinable, int minEmpty)
        {
            var tables = tier as IReadOnlyCollection<TableCapacity> ?? tier?.ToList() ?? (IReadOnlyCollection<TableCapacity>)Array.Empty<TableCapacity>();

            int total    = tables.Count;
            int joinable = tables.Count(t => t.IsJoinable);
            int empty    = tables.Count(t => t.IsEmpty);

            int deficit = Math.Max(Math.Max(minTables - total, minJoinable - joinable), minEmpty - empty);
            return Math.Max(0, deficit);
        }

        /// <summary>
        /// Which tables this tier can give up, oldest-empty first.
        ///
        /// Only empty tables are ever removed, and only once they have been empty for <paramref name="grace"/> —
        /// without that, a table would vanish the instant its last player stood up, and reappear a second later when
        /// the next one arrived. Removal stops the moment it would breach any floor, so a tier can shrink back to its
        /// resting size but never below it.
        /// </summary>
        public static IReadOnlyList<string> TablesToRemove(
            IEnumerable<TableCapacity> tier, int minTables, int minJoinable, int minEmpty,
            TimeSpan grace, DateTimeOffset now)
        {
            var tables = tier?.ToList() ?? new List<TableCapacity>();

            int total    = tables.Count;
            int joinable = tables.Count(t => t.IsJoinable);
            int empty    = tables.Count(t => t.IsEmpty);

            // Longest-empty first: the least likely to be missed, and it keeps table ids stable for anyone browsing.
            var candidates = tables
                .Where(t => t.IsEmpty && t.EmptySince.HasValue && now - t.EmptySince.Value >= grace)
                .OrderBy(t => t.EmptySince.Value)
                .ToList();

            var doomed = new List<string>();
            foreach (var t in candidates)
            {
                // An empty table is also a joinable one, so dropping it costs a unit of all three floors.
                if (total - 1 < minTables) break;
                if (joinable - 1 < minJoinable) break;
                if (empty - 1 < minEmpty) break;

                doomed.Add(t.TableId);
                total--; joinable--; empty--;
            }
            return doomed;
        }

        /// <summary>
        /// Choose which of a tier's tables to actually send to the client.
        ///
        /// A busy tier is thirty tables and a carousel is not a list — sending all of them is unusable to swipe
        /// through and pointless payload. So the page is COMPOSED rather than merely truncated:
        ///
        ///   • a few FULL tables, because a casino with no full tables looks dead;
        ///   • mostly tables with a seat or two free — the ones a player actually wants, since joining those puts
        ///     them among people rather than alone;
        ///   • and always at least one COMPLETELY EMPTY table, kept even when that means dropping a busier one,
        ///     so "I want a table to myself" is never impossible just because the tier is popular.
        ///
        /// Returned in display order: fullest first, empty last.
        /// </summary>
        public static IReadOnlyList<T> Page<T>(
            IEnumerable<T> tier, Func<T, TableCapacity> capacityOf,
            int pageSize, int fullQuota, int emptyQuota)
        {
            var all = tier?.ToList() ?? new List<T>();
            if (pageSize <= 0 || all.Count <= pageSize) return Display(all, capacityOf);

            var full = all.Where(t => !capacityOf(t).IsJoinable).ToList();
            var empty = all.Where(t => capacityOf(t).IsEmpty).ToList();
            var partial = all.Where(t => { var c = capacityOf(t); return c.IsJoinable && !c.IsEmpty; })
                             .OrderByDescending(t => capacityOf(t).Occupied)
                             .ToList();

            // Reserve the empty slots FIRST — they are the ones a naive "busiest N" would always discard.
            int keepEmpty = Math.Min(Math.Max(0, emptyQuota), empty.Count);
            int budget = Math.Max(0, pageSize - keepEmpty);

            var picked = new List<T>(pageSize);
            picked.AddRange(full.Take(Math.Min(Math.Max(0, fullQuota), budget)));
            picked.AddRange(partial.Take(Math.Max(0, budget - picked.Count)));

            // Short of the budget (few partials about) — spend what is left on more full tables, then more empties.
            if (picked.Count < budget) picked.AddRange(full.Skip(picked.Count(t => !capacityOf(t).IsJoinable))
                                                          .Take(budget - picked.Count));
            if (picked.Count < budget) picked.AddRange(empty.Skip(keepEmpty).Take(budget - picked.Count));

            picked.AddRange(empty.Take(keepEmpty));
            return Display(picked, capacityOf);
        }

        private static IReadOnlyList<T> Display<T>(IEnumerable<T> items, Func<T, TableCapacity> capacityOf)
            => items.OrderBy(t => capacityOf(t).IsEmpty ? 1 : 0)
                    .ThenByDescending(t => capacityOf(t).Occupied)
                    .ThenBy(t => capacityOf(t).TableId, StringComparer.Ordinal)
                    .ToList();

        /// <summary>
        /// The order the lobby shows a tier in: fullest first, completely empty tables last.
        ///
        /// This is what puts the "play alone" table at the end. It also nudges arriving players towards tables that
        /// already have people at them — with the carousel joining whatever is on screen, ordering IS the matchmaking.
        /// </summary>
        public static IReadOnlyList<T> ForDisplay<T>(IEnumerable<T> tier, Func<T, TableCapacity> capacityOf)
        {
            return tier
                .OrderBy(t => capacityOf(t).IsEmpty ? 1 : 0)          // empty tables sink to the bottom
                .ThenByDescending(t => capacityOf(t).Occupied)        // then fullest first, so players cluster
                .ThenBy(t => capacityOf(t).TableId, StringComparer.Ordinal)   // stable tie-break
                .ToList();
        }
    }
}
