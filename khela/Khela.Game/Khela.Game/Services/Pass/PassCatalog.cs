using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Khela.Common.Rewards;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Rewards;

namespace Khela.Game.Services.Pass
{
    /// <summary>How a program's periods are laid out in time.</summary>
    public enum PassCadence
    {
        /// <summary>Cycles generate themselves: the 1st at 00:00 UTC to the 1st of the next month.
        /// Cycle key "yyyy-MM". This is the Monthly Pass — it renews with no admin action.</summary>
        Monthly = 0,

        /// <summary>ONE explicit window. What a future Season Pass (not a month long) will use.</summary>
        Fixed = 1,
    }

    /// <summary>Who may claim a node whose day has already passed.</summary>
    public enum CatchUpPolicy
    {
        /// <summary>Today's node only, both tracks. Miss a day, lose it.</summary>
        None = 0,

        /// <summary>DEFAULT. Free: today only. Golden: every earlier node of the cycle unlocks —
        /// catching up on missed days is part of what the subscription BUYS.</summary>
        GoldenOnly = 1,

        /// <summary>Both tracks backfill the whole cycle.</summary>
        All = 2,
    }

    /// <summary>One step of the ladder. <see cref="Index"/> IS the day of the cycle — day 7 is node 7.</summary>
    public sealed class PassNode
    {
        public int Index { get; set; }
        public bool IsMilestone { get; set; }           // UI emphasis only — no payout meaning
        public List<RewardGrant> Free { get; set; } = new List<RewardGrant>();
        public List<RewardGrant> Golden { get; set; } = new List<RewardGrant>();
    }

    /// <summary>A ladder that replaces the recurring one for exactly one cycle (a themed December).</summary>
    public sealed class PassCycleOverride
    {
        public string CycleKey { get; set; }            // "2026-12"
        public string Title { get; set; }
        public List<PassNode> Nodes { get; set; } = new List<PassNode>();
    }

    /// <summary>
    /// A pass PROGRAM. <c>monthly</c> is the one built now; a Season Pass is a separate program with its own key,
    /// cadence and ladder — every claim/entitlement row carries the key, so adding one is config, not a migration.
    /// Golden is sold for REAL MONEY only (an auto-renewing store subscription) — there is deliberately no
    /// in-game-currency price field anywhere in this model.
    /// </summary>
    public sealed class PassProgram
    {
        public string Key { get; set; }                 // stable id, e.g. "monthly" — referenced by claim rows forever
        public string Title { get; set; }
        public bool Enabled { get; set; } = true;
        public PassCadence Cadence { get; set; } = PassCadence.Monthly;
        public CatchUpPolicy CatchUp { get; set; } = CatchUpPolicy.GoldenOnly;

        public DateTime? StartUtc { get; set; }         // Fixed cadence only
        public DateTime? EndUtc { get; set; }           // Fixed cadence only, exclusive

        public string GoldenProductIdApple { get; set; }    // App Store subscription product id
        public string GoldenProductIdGoogle { get; set; }   // Play Store subscription product id
        public decimal GoldenPriceUsd { get; set; }         // DISPLAY fallback only; the store's localized price wins

        public List<PassNode> Nodes { get; set; } = new List<PassNode>();                    // the RECURRING ladder
        public List<PassCycleOverride> CycleOverrides { get; set; } = new List<PassCycleOverride>();

        public bool SellsGolden => !string.IsNullOrWhiteSpace(GoldenProductIdApple) || !string.IsNullOrWhiteSpace(GoldenProductIdGoogle);
    }

    /// <summary>One resolved period of a program — the thing a player actually plays.</summary>
    public sealed class PassCycle
    {
        public string PassKey { get; set; }
        public string CycleKey { get; set; }            // "2026-09" (monthly) or the program key (fixed)
        public string Title { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }            // exclusive
        public CatchUpPolicy CatchUp { get; set; }
        public List<PassNode> Nodes { get; set; } = new List<PassNode>();   // already trimmed to the cycle's length

        /// <summary>Days in this cycle — 28..31 for a month.</summary>
        public int Days => (int)Math.Round((EndUtc - StartUtc).TotalDays);

        /// <summary>Ladder length actually reachable this cycle (nodes are trimmed to <see cref="Days"/>).</summary>
        public int Length => Nodes?.Count ?? 0;

        public PassNode Node(int index) => Nodes?.FirstOrDefault(n => n.Index == index);

        /// <summary>1-based day of the cycle at <paramref name="nowUtc"/> (clamped to the cycle).</summary>
        public int DayIndex(DateTime nowUtc)
            => Math.Max(1, Math.Min(Days, (int)(nowUtc.Date - StartUtc.Date).TotalDays + 1));

        /// <summary>The highest node claimable at <paramref name="nowUtc"/> — never ahead of the calendar.</summary>
        public int MaxNode(DateTime nowUtc) => Math.Min(DayIndex(nowUtc), Length);
    }

    /// <summary>The pass config (overlay shape). Admin-editable JSON in Redis <c>khela:pass</c>; falls back to
    /// <see cref="PassCatalog.Defaults"/>.</summary>
    public sealed class PassConfig
    {
        public bool Enabled { get; set; } = true;
        public List<PassProgram> Programs { get; set; } = new List<PassProgram>();

        public PassProgram Find(string key)
            => string.IsNullOrEmpty(key) ? null
             : Programs?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>The program to run when the caller didn't name one: <see cref="PassCatalog.MonthlyKey"/> if it's
        /// enabled, else the first enabled program.</summary>
        public PassProgram Default()
            => !Enabled ? null
             : (Find(PassCatalog.MonthlyKey) is PassProgram m && m.Enabled) ? m
             : Programs?.FirstOrDefault(p => p.Enabled);
    }

    /// <summary>
    /// The pass catalog: defaults, parsing, cycle resolution and the admin-save validation. PURE — no DB, no Redis,
    /// no wallet — so the whole rule set is unit-testable. Mirrors <see cref="ChestCatalog"/>: the effective config is
    /// the Redis overlay if it parses, else <see cref="Defaults"/>. See docs/PASS_SPEC.md §3.
    /// </summary>
    public static class PassCatalog
    {
        /// <summary>The Redis key holding the admin-edited pass config JSON (overlay; absent ⇒ code defaults).</summary>
        public const string RedisKey = "khela:pass";

        /// <summary>The built-in monthly program's key. Claim rows reference it forever — never rename it.</summary>
        public const string MonthlyKey = "monthly";

        /// <summary>Nodes in the seed ladder. 28 are reachable in EVERY month; 29–31 are bonus days.</summary>
        public const int DefaultLadderLength = 31;

        /// <summary>The last day-of-month that exists in every month — the highest index a milestone may sit on,
        /// and the most nodes a monthly ladder can guarantee.</summary>
        public const int GuaranteedDays = 28;

        /// <summary>Hard ceiling on a monthly ladder (the longest month).</summary>
        public const int MaxMonthlyNodes = 31;

        /// <summary>Ceiling for a Fixed-cadence program (a Season Pass can be longer than a month).</summary>
        public const int MaxFixedNodes = 366;

        /// <summary>Upper bound on one chest line's count (matches the granter's own clamp).</summary>
        public const int MaxChestsPerLine = 20;

        /// <summary>Max length of the <c>PassKey</c> / <c>CycleKey</c> columns.</summary>
        public const int MaxKeyLength = 32;

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>
        /// The built-in config: the monthly program with the seed ladder. Its cycles generate themselves from the
        /// calendar, so a fresh deployment has a live, self-renewing pass with no authoring step and no expiry date.
        /// Amounts are anchored to what daily missions and the common chest already pay; the real ladder is authored
        /// in the admin panel, which overrides this wholesale.
        /// </summary>
        public static PassConfig Defaults() => new PassConfig
        {
            Enabled = true,
            Programs = new List<PassProgram> { MonthlyProgram() },
        };

        /// <summary>The seed monthly program — also what the admin panel's "create program" starts from.</summary>
        public static PassProgram MonthlyProgram() => new PassProgram
        {
            Key = MonthlyKey,
            Title = "Monthly Pass",
            Enabled = true,
            Cadence = PassCadence.Monthly,
            CatchUp = CatchUpPolicy.GoldenOnly,
            GoldenProductIdApple = "khela.pass.golden.monthly",
            GoldenProductIdGoogle = "khela.pass.golden.monthly",
            GoldenPriceUsd = 4.99m,
            Nodes = DefaultLadder(),
        };

        /// <summary>The seed ladder (docs/PASS_SPEC.md §3.1). The closing milestone sits on node 28 so it is reachable
        /// in February; 29–31 are bonus days that simply don't exist in shorter months.</summary>
        public static List<PassNode> DefaultLadder()
        {
            var nodes = new List<PassNode>(DefaultLadderLength);
            for (int i = 1; i <= DefaultLadderLength; i++)
            {
                var node = new PassNode { Index = i };
                switch (i)
                {
                    case 10:
                        node.IsMilestone = true;
                        node.Free = Lines(Chips(3000), Kash(10), Xp(150));
                        node.Golden = Lines(RewardGrant.Chest("CK_Chest:Uncommon"), Kash(25), Xp(300));
                        break;
                    case 20:
                        node.IsMilestone = true;
                        node.Free = Lines(Chips(5000), Kash(20), Xp(250));
                        node.Golden = Lines(RewardGrant.Chest("CK_Chest:Rare"), Kash(50), Xp(500));
                        break;
                    case GuaranteedDays:
                        node.IsMilestone = true;
                        node.Free = Lines(Chips(10000), Kash(30), Xp(500));
                        node.Golden = Lines(RewardGrant.Chest("CK_Chest:Rare"), Kash(150), Xp(1000));
                        break;
                    default:
                        if (i <= 5)       { node.Free = Lines(Chips(1000), Xp(50));  node.Golden = Lines(Chips(3000),  Kash(5),  Xp(100)); }
                        else if (i <= 9)  { node.Free = Lines(Chips(1500), Xp(75));  node.Golden = Lines(Chips(5000),  Kash(10), Xp(150)); }
                        else if (i <= 19) { node.Free = Lines(Chips(2000), Xp(100)); node.Golden = Lines(Chips(7500),  Kash(15), Xp(200)); }
                        else              { node.Free = Lines(Chips(2500), Xp(125)); node.Golden = Lines(Chips(10000), Kash(20), Xp(250)); }
                        break;
                }
                nodes.Add(node);
            }
            return nodes;
        }

        // ---- cycle resolution ----

        /// <summary>The cycle key for a monthly program at <paramref name="nowUtc"/> ("yyyy-MM").</summary>
        public static string MonthlyCycleKey(DateTime nowUtc) => nowUtc.ToString("yyyy-MM");

        /// <summary>
        /// The period of <paramref name="program"/> in effect at <paramref name="nowUtc"/>, with its ladder resolved
        /// (per-cycle override if one exists, else the recurring ladder) and TRIMMED to the days the cycle actually
        /// has — so a 31-node ladder stops at 28 in February and nobody is shown a node they can never reach.
        /// Null when the program is disabled or the moment falls outside a Fixed window ⇒ the pass is simply off.
        /// </summary>
        public static PassCycle CurrentCycle(PassProgram program, DateTime nowUtc)
        {
            if (program == null || !program.Enabled) return null;

            DateTime start, end;
            string cycleKey;
            if (program.Cadence == PassCadence.Monthly)
            {
                start = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                end = start.AddMonths(1);
                cycleKey = MonthlyCycleKey(nowUtc);
            }
            else
            {
                if (!program.StartUtc.HasValue || !program.EndUtc.HasValue) return null;
                start = program.StartUtc.Value;
                end = program.EndUtc.Value;
                if (end <= start || nowUtc < start || nowUtc >= end) return null;
                cycleKey = program.Key;
            }

            var over = program.CycleOverrides?.FirstOrDefault(o => string.Equals(o.CycleKey, cycleKey, StringComparison.OrdinalIgnoreCase));
            var ladder = (over?.Nodes != null && over.Nodes.Count > 0) ? over.Nodes : program.Nodes;
            var days = (int)Math.Round((end - start).TotalDays);

            return new PassCycle
            {
                PassKey = program.Key,
                CycleKey = cycleKey,
                Title = over?.Title ?? program.Title,
                StartUtc = start,
                EndUtc = end,
                CatchUp = program.CatchUp,
                Nodes = (ladder ?? new List<PassNode>()).Where(n => n.Index >= 1 && n.Index <= days)
                                                        .OrderBy(n => n.Index).ToList(),
            };
        }

        /// <summary>
        /// Which nodes the player may claim right now: everything up to today's index that they haven't claimed,
        /// filtered by the cycle's <see cref="CatchUpPolicy"/>. PURE — the caller supplies what's already claimed and
        /// whether the player is golden, so the whole rule is testable without a database.
        /// </summary>
        public static List<int> ClaimableNodes(PassCycle cycle, DateTime nowUtc, ISet<int> alreadyClaimed, bool isGolden)
        {
            var claimable = new List<int>();
            if (cycle == null) return claimable;

            int maxNode = cycle.MaxNode(nowUtc);
            bool backfill = cycle.CatchUp == CatchUpPolicy.All || (cycle.CatchUp == CatchUpPolicy.GoldenOnly && isGolden);

            for (int i = backfill ? 1 : maxNode; i <= maxNode; i++)
            {
                if (i < 1) continue;
                if (alreadyClaimed != null && alreadyClaimed.Contains(i)) continue;
                if (cycle.Node(i) == null) continue;
                claimable.Add(i);
            }
            return claimable;
        }

        /// <summary>How many past nodes a subscription would unlock right now — the buy CTA's number ("unlock N missed
        /// days"). Zero when the player is already golden or the policy doesn't gate catch-up.</summary>
        public static int LockedByGolden(PassCycle cycle, DateTime nowUtc, ISet<int> alreadyClaimed, bool isGolden)
        {
            if (cycle == null || isGolden || cycle.CatchUp != CatchUpPolicy.GoldenOnly) return 0;
            int maxNode = cycle.MaxNode(nowUtc), locked = 0;
            for (int i = 1; i < maxNode; i++)
                if (cycle.Node(i) != null && (alreadyClaimed == null || !alreadyClaimed.Contains(i))) locked++;
            return locked;
        }

        // ---- persistence ----

        /// <summary>Serialize a config the way the admin editor and the Redis overlay store it (indented, enums as
        /// names). Paired with <see cref="TryParse"/> so a save/load round-trips exactly.</summary>
        public static string ToJson(PassConfig cfg) => JsonSerializer.Serialize(cfg, JsonOptions);

        /// <summary>Parse an admin override JSON; null if blank/invalid (the caller falls back to defaults).</summary>
        public static PassConfig TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var cfg = JsonSerializer.Deserialize<PassConfig>(json, JsonOptions);
                return (cfg?.Programs != null) ? cfg : null;   // zero programs is legal — it just means "off"
            }
            catch { return null; }
        }

        // ---- validation ----

        /// <summary>
        /// Validate a config for the admin save. Returns the FIRST error message, or null if valid.
        ///
        /// <paramref name="chests"/> (optional) checks chest rewards against the real chest catalog, and
        /// <paramref name="payableKinds"/> (optional, from <c>IRewardGrantService.CanGrant</c>) refuses a reward this
        /// build cannot actually pay out — catching both at AUTHORING time rather than silently skipping at claim time.
        /// </summary>
        public static string Validate(PassConfig cfg, ChestConfig chests = null, ISet<RewardKind> payableKinds = null)
        {
            if (cfg == null) return "No config.";
            if (cfg.Programs == null) return "Programs is missing.";

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in cfg.Programs)
            {
                if (string.IsNullOrWhiteSpace(p.Key)) return "Every pass program needs a Key (e.g. \"monthly\").";
                if (p.Key.Length > MaxKeyLength) return $"Program key '{p.Key}' is longer than {MaxKeyLength} characters.";
                if (!keys.Add(p.Key)) return $"Duplicate program key: {p.Key}.";
                if (p.GoldenPriceUsd < 0m) return $"{p.Key}: price can't be negative.";

                if (p.Cadence == PassCadence.Fixed)
                {
                    if (!p.StartUtc.HasValue || !p.EndUtc.HasValue) return $"{p.Key}: a fixed-window program needs a start and an end.";
                    if (p.EndUtc.Value <= p.StartUtc.Value) return $"{p.Key}: the window ends before it starts.";
                }

                var err = ValidateLadder(p, p.Key, p.Nodes, chests, payableKinds);
                if (err != null) return err;

                foreach (var o in p.CycleOverrides ?? new List<PassCycleOverride>())
                {
                    if (string.IsNullOrWhiteSpace(o.CycleKey)) return $"{p.Key}: a cycle override needs a cycle key (e.g. \"2026-12\").";
                    if (o.CycleKey.Length > MaxKeyLength) return $"{p.Key}: cycle key '{o.CycleKey}' is longer than {MaxKeyLength} characters.";
                    if (p.Cadence == PassCadence.Monthly && !IsMonthlyCycleKey(o.CycleKey))
                        return $"{p.Key}: cycle override key '{o.CycleKey}' must look like \"2026-12\".";
                    err = ValidateLadder(p, $"{p.Key}/{o.CycleKey}", o.Nodes, chests, payableKinds);
                    if (err != null) return err;
                }
            }
            return null;
        }

        /// <summary>True for a well-formed monthly cycle key ("yyyy-MM").</summary>
        public static bool IsMonthlyCycleKey(string key)
            => DateTime.TryParseExact(key, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.None, out _);

        private static string ValidateLadder(PassProgram program, string label, List<PassNode> nodes,
            ChestConfig chests, ISet<RewardKind> payableKinds)
        {
            if (nodes == null || nodes.Count == 0) return $"{label}: add at least one node.";

            int max = program.Cadence == PassCadence.Monthly ? MaxMonthlyNodes : MaxFixedNodes;
            if (nodes.Count > max) return $"{label}: {nodes.Count} nodes exceeds the {max} allowed for a {program.Cadence.ToString().ToLowerInvariant()} pass.";

            // The ladder must be exactly 1..N: the node index IS the day of the cycle, so a gap or a duplicate would
            // silently strand a day nobody can ever claim.
            var indexes = nodes.Select(n => n.Index).OrderBy(i => i).ToList();
            for (int i = 0; i < indexes.Count; i++)
                if (indexes[i] != i + 1)
                    return $"{label}: node indexes must run 1..{indexes.Count} with no gaps or duplicates (found {indexes[i]} at position {i + 1}).";

            bool paysGolden = false;
            foreach (var n in nodes)
            {
                int free = n.Free?.Count ?? 0, golden = n.Golden?.Count ?? 0;
                if (free == 0 && golden == 0) return $"{label} node {n.Index}: give it a Free or Golden reward (or remove it).";
                if (golden > 0) paysGolden = true;

                // A milestone above day 28 is unreachable in February — that's a design mistake, not a preference.
                if (n.IsMilestone && program.Cadence == PassCadence.Monthly && n.Index > GuaranteedDays)
                    return $"{label} node {n.Index}: a milestone above day {GuaranteedDays} can't be reached in February — move it down.";

                var err = ValidateLines(label, n.Index, "Free", n.Free, chests, payableKinds)
                       ?? ValidateLines(label, n.Index, "Golden", n.Golden, chests, payableKinds);
                if (err != null) return err;
            }

            // Golden rewards a player can never buy are a support ticket, not a config.
            if (paysGolden && !program.SellsGolden)
                return $"{label}: the golden track has rewards but no store product id — set the Apple and/or Google product.";

            return null;
        }

        private static string ValidateLines(string label, int node, string track, List<RewardGrant> lines,
            ChestConfig chests, ISet<RewardKind> payableKinds)
        {
            if (lines == null) return null;
            string Where(string msg) => $"{label} node {node} ({track}): {msg}";

            foreach (var line in lines)
            {
                if (line == null) return Where("empty reward line.");
                if (!Enum.IsDefined(typeof(RewardKind), line.Kind)) return Where($"'{(int)line.Kind}' is not a known reward kind.");
                if (line.Amount <= 0m) return Where($"{line.Kind} amount must be greater than 0.");
                if (payableKinds != null && !payableKinds.Contains(line.Kind))
                    return Where($"{line.Kind} rewards can't be paid out by this build yet.");

                switch (line.Kind)
                {
                    case RewardKind.Currency:
                        if (!RewardCurrencies.TryParse(line.Id, out var currency))
                            return Where($"'{line.Id}' is not a currency name (allowed: {RewardCurrencies.AllowedList}).");
                        if (!RewardCurrencies.IsAllowed(currency))
                            return Where(RewardCurrencies.IsForbidden(currency)
                                ? $"'{currency}' can never be a reward (tradeable token)."
                                : $"'{currency}' is not a permitted reward currency (allowed: {RewardCurrencies.AllowedList}).");
                        break;

                    case RewardKind.Chest:
                        if (!RewardIds.TryParseChest(line.Id, out var chestKey, out var tier))
                            return Where($"'{line.Id}' is not a chest id — use \"Key:Tier\", e.g. \"CK_Chest:Rare\".");
                        if (chests != null && chests.Find(chestKey, tier) == null)
                            return Where($"chest '{line.Id}' isn't in the chest catalog.");
                        if (line.Amount > MaxChestsPerLine) return Where($"at most {MaxChestsPerLine} chests per line.");
                        break;

                    case RewardKind.Cosmetic:
                    case RewardKind.Item:
                        if (string.IsNullOrWhiteSpace(line.Id)) return Where($"{line.Kind} rewards need an id.");
                        break;
                }
            }
            return null;
        }

        /// <summary>Ladder payout totals for one track, keyed by a display label ("Chips", "Kash", "XP",
        /// "CK_Chest:Rare"). Pure — the admin editor shows this while authoring so the cost is visible up front.</summary>
        public static Dictionary<string, decimal> Totals(IEnumerable<PassNode> nodes, bool golden)
        {
            var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes ?? Enumerable.Empty<PassNode>())
                foreach (var line in (golden ? node.Golden : node.Free) ?? new List<RewardGrant>())
                {
                    var label = line.Kind == RewardKind.Xp ? "XP" : (line.Id ?? line.Kind.ToString());
                    totals[label] = (totals.TryGetValue(label, out var v) ? v : 0m) + line.Amount;
                }
            return totals;
        }

        // ---- authoring helpers (also used by the admin editor's bulk tools) ----

        private static List<RewardGrant> Lines(params RewardGrant[] lines) => new List<RewardGrant>(lines);
        private static RewardGrant Chips(decimal amount) => RewardGrant.Currency("Chips", amount);
        private static RewardGrant Kash(decimal amount) => RewardGrant.Currency("Kash", amount);
        private static RewardGrant Xp(long amount) => RewardGrant.Xp(amount);
    }
}
