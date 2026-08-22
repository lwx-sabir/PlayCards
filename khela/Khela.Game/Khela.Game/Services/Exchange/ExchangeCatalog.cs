using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Khela.Game.Database.Models;
using Khela.Game.Services.Rewards;

namespace Khela.Game.Services.Exchange
{
    /// <summary>
    /// One exchange pair: <see cref="FromCurrency"/> → <see cref="ToCurrency"/> at <see cref="FromPerUnit"/> units of FROM
    /// per ONE unit of TO. The rate is stored that way round — and the player always chooses the TO amount — because it is
    /// the only form in which 1,000,000 chips = 1 Kash has NO rounding: cost = toAmount × FromPerUnit, exactly. A rate
    /// stored as "Kash per chip" would be 0.000001 and every quote would be a floor somewhere.
    /// Any pair of wallet currencies is allowed EXCEPT Tokens (never exchangeable, never wagered — the legal guardrail), and
    /// a pair plus its reverse must be LOSSY round-trip (see <see cref="ExchangeCatalog.Validate"/>).
    /// </summary>
    public sealed class ExchangePairDef
    {
        public string Key { get; set; }
        public bool Enabled { get; set; } = true;
        public string Title { get; set; }
        public string Description { get; set; }
        /// <summary>Wallet currency NAMES ("Chips", "Kash", "Gems", "Coins").</summary>
        public string FromCurrency { get; set; }
        public string ToCurrency { get; set; }
        /// <summary>Units of FROM per one unit of TO.</summary>
        public decimal FromPerUnit { get; set; }
        /// <summary>Granularity of the TO amount (1 = whole units). The TO amount must be a multiple.</summary>
        public decimal Step { get; set; } = 1m;
        public decimal MinTo { get; set; } = 1m;
        /// <summary>0 = no per-exchange ceiling.</summary>
        public decimal MaxToPerTx { get; set; }
        /// <summary>0 = uncapped; per UTC day, per player, in TO units.</summary>
        public decimal DailyCapTo { get; set; }
        /// <summary>0 = uncapped; lifetime, per player, in TO units.</summary>
        public decimal LifetimeCapTo { get; set; }
        public int MinLevel { get; set; }
        public int SortOrder { get; set; }
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
    }

    /// <summary>The exchange document (overlay shape). Admin-edited JSON in Redis <c>khela:exchange</c>; falls back to <see cref="ExchangeCatalog.Defaults"/>.</summary>
    public sealed class ExchangeCatalogConfig
    {
        public int Version { get; set; } = 1;
        /// <summary>Authored switch (the runtime kill switch is <c>Exchange:Enabled</c> on the settings overlay).</summary>
        public bool Enabled { get; set; } = true;
        public List<ExchangePairDef> Pairs { get; set; } = new List<ExchangePairDef>();

        public ExchangePairDef Find(string key)
            => string.IsNullOrWhiteSpace(key) ? null
             : Pairs?.FirstOrDefault(p => p != null && string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The exchange catalog: Redis key, JSON round-trip, the seed (DECISIONS E-03: chips → Kash, 1,000,000 : 1, one way), the
    /// fail-closed validator the admin save and the service both run, and the arithmetic. Pure — no I/O — so it is unit-tested
    /// (ExchangeCatalogTests). Full spec: docs/EXCHANGE_SPEC.md.
    /// </summary>
    public static class ExchangeCatalog
    {
        public const string RedisKey = "khela:exchange";
        /// <summary>Runtime kill switch on the <c>khela:settings</c> hash (admin); default on.</summary>
        public const string EnabledSwitch = "Exchange:Enabled";

        public const int MaxKeyLength = 32;
        public const int MaxTitleLength = 60;
        public const int MaxDescriptionLength = 200;

        /// <summary>
        /// The wallet's scale: balances and ledger amounts are <c>decimal(18,4)</c>. Every amount that reaches the wallet must be a
        /// whole number of these — MySQL silently ROUNDS a finer value (0.00005 → 0.0000, a note, not an error), and a debit that
        /// rounds to zero while the credit lands is minting. So every TO quantity and every COST is required to be exact at this
        /// scale by the validator (<see cref="Validate"/>) and re-checked on the way in (<see cref="Refusal"/>).
        /// </summary>
        public const decimal WalletQuantum = 0.0001m;

        /// <summary>Is <paramref name="x"/> a whole number of wallet quanta (representable in decimal(18,4))?</summary>
        public static bool Representable(decimal x) => decimal.Remainder(x, WalletQuantum) == 0m;

        private static readonly Regex KeyPattern = new Regex("^[a-z0-9_]+$", RegexOptions.Compiled);

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        // ---------------------------------------------------------------- defaults (DECISIONS E-03)

        /// <summary>
        /// Chips → Kash at 1,000,000 : 1, one way. Bought chips (5M / $1.99) convert to 2.5 Kash/$ against 50 Kash/$ direct —
        /// 20× worse, never an arbitrage; the pair exists as the chip SINK the economy pegs to (khela-economy-audit), not as a
        /// way to buy Kash. The real ladder is authored in the admin, which overrides this wholesale.
        /// </summary>
        public static ExchangeCatalogConfig Defaults() => new ExchangeCatalogConfig
        {
            Version = 1,
            Enabled = true,
            Pairs =
            {
                new ExchangePairDef
                {
                    Key = "chips_kash", Enabled = true, SortOrder = 10,
                    Title = "Chips → Kash", Description = "1,000,000 chips buys 1 Kash. One way — Kash never converts back.",
                    FromCurrency = "Chips", ToCurrency = "Kash", FromPerUnit = 1_000_000m,
                    Step = 1m, MinTo = 1m, MaxToPerTx = 100m, DailyCapTo = 0m, LifetimeCapTo = 0m, MinLevel = 0,
                },
            },
        };

        // ---------------------------------------------------------------- JSON

        public static string ToJson(ExchangeCatalogConfig cfg) => JsonSerializer.Serialize(cfg, JsonOptions);

        /// <summary>Parse an admin override; null if blank/invalid JSON. Does NOT validate — pair with <see cref="Validate"/>.</summary>
        public static ExchangeCatalogConfig TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var cfg = JsonSerializer.Deserialize<ExchangeCatalogConfig>(json, JsonOptions);
                if (cfg?.Pairs == null) return null;   // zero pairs is legal — "no exchange offered"
                return cfg;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- arithmetic

        /// <summary>Is <paramref name="toAmount"/> a whole number of the pair's steps? (0 and negatives are not.)</summary>
        public static bool AlignsToStep(ExchangePairDef pair, decimal toAmount)
        {
            if (pair == null || toAmount <= 0m) return false;
            var step = pair.Step <= 0m ? 1m : pair.Step;
            return decimal.Remainder(toAmount, step) == 0m;
        }

        /// <summary>What <paramref name="toAmount"/> of TO costs in FROM — exact, no rounding: toAmount × FromPerUnit.</summary>
        public static decimal Cost(ExchangePairDef pair, decimal toAmount) => toAmount * pair.FromPerUnit;

        /// <summary>
        /// Why a player can't run <paramref name="pair"/> for <paramref name="toAmount"/> right now — null when they can.
        /// Pure on its inputs: the pair, the amount, what the player already took through it (today / ever, TO units), level, clock.
        /// </summary>
        public static string Refusal(ExchangePairDef pair, decimal toAmount, decimal usedToday, decimal usedLifetime, int level, DateTime nowUtc)
        {
            if (pair == null || !pair.Enabled) return "This exchange is not available.";
            if (pair.FromUtc.HasValue && nowUtc < pair.FromUtc.Value) return "Not available yet.";
            if (pair.ToUtc.HasValue && nowUtc >= pair.ToUtc.Value) return "This exchange has ended.";
            if (pair.MinLevel > 0 && level < pair.MinLevel) return $"Unlocks at level {pair.MinLevel}.";
            if (toAmount <= 0m) return "Choose an amount.";
            if (!AlignsToStep(pair, toAmount)) return $"Amount must be a multiple of {pair.Step:0.####}.";
            if (toAmount < pair.MinTo) return $"Minimum is {pair.MinTo:#,0.####} {pair.ToCurrency}.";
            if (pair.MaxToPerTx > 0m && toAmount > pair.MaxToPerTx) return $"Maximum per exchange is {pair.MaxToPerTx:#,0.####} {pair.ToCurrency}.";
            // Belt-and-braces on the validator's quantum rule: a cost the wallet can't hold exactly would be ROUNDED by MySQL —
            // a debit rounding to zero while the credit lands is minting. Refuse rather than trust the catalog.
            var cost = Cost(pair, toAmount);
            if (cost < WalletQuantum || !Representable(cost) || !Representable(toAmount)) return "This amount can't be exchanged at this rate.";
            if (pair.DailyCapTo > 0m && usedToday + toAmount > pair.DailyCapTo)
                return usedToday >= pair.DailyCapTo ? "Daily limit reached — come back tomorrow." : $"Only {pair.DailyCapTo - usedToday:#,0.####} {pair.ToCurrency} left today.";
            if (pair.LifetimeCapTo > 0m && usedLifetime + toAmount > pair.LifetimeCapTo)
                return usedLifetime >= pair.LifetimeCapTo ? "Limit reached." : $"Only {pair.LifetimeCapTo - usedLifetime:#,0.####} {pair.ToCurrency} left.";
            return null;
        }

        /// <summary>
        /// Would A→B followed by B→A hand back MORE than was put in? With rates expressed as FROM-per-unit-of-TO, one B
        /// buys 1/F_ba A, which buys 1/(F_ba·F_ab) B — so the round trip is profitable exactly when F_ab × F_ba &lt; 1 and
        /// break-even at 1. An exchange must be LOSSY both ways, or it is a money printer with two steps.
        /// </summary>
        public static bool RoundTripNotLossy(decimal fromPerUnitAB, decimal fromPerUnitBA)
            => fromPerUnitAB > 0m && fromPerUnitBA > 0m && fromPerUnitAB * fromPerUnitBA <= 1m;

        // ---------------------------------------------------------------- validation (fail closed)

        /// <summary>Validate a catalog for the admin save / the effective-config load. Returns the FIRST error, or null.</summary>
        public static string Validate(ExchangeCatalogConfig cfg)
        {
            if (cfg == null) return "Empty catalog.";
            if (cfg.Version < 1) return "Version must be ≥ 1.";
            if (cfg.Pairs == null) return "Pairs missing.";

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byRoute = new Dictionary<string, ExchangePairDef>(StringComparer.OrdinalIgnoreCase);   // "Chips>Kash" → pair
            foreach (var p in cfg.Pairs)
            {
                if (p == null) return "A pair entry is null.";
                if (string.IsNullOrWhiteSpace(p.Key)) return "A pair has no key.";
                var key = p.Key.Trim();
                if (key.Length > MaxKeyLength) return $"{key}: key longer than {MaxKeyLength}.";
                if (!KeyPattern.IsMatch(key)) return $"{key}: keys may only contain a-z, 0-9 and '_'.";
                if (!keys.Add(key)) return $"{key}: duplicate pair key.";
                p.Key = key;   // canonical — Find() and the admin's forms match the exact stored value
                if ((p.Title ?? "").Length > MaxTitleLength) return $"{key}: title longer than {MaxTitleLength}.";
                if ((p.Description ?? "").Length > MaxDescriptionLength) return $"{key}: description longer than {MaxDescriptionLength}.";

                // Currencies: wallet names, never Tokens (the legal guardrail), never the same on both sides.
                if (!RewardCurrencies.TryParse(p.FromCurrency, out var from)) return $"{key}: '{p.FromCurrency}' is not a currency name.";
                if (!RewardCurrencies.IsAllowed(from)) return $"{key}: {from} can never be exchanged (allowed: {RewardCurrencies.AllowedList}).";
                if (!RewardCurrencies.TryParse(p.ToCurrency, out var to)) return $"{key}: '{p.ToCurrency}' is not a currency name.";
                if (!RewardCurrencies.IsAllowed(to)) return $"{key}: {to} can never be exchanged (allowed: {RewardCurrencies.AllowedList}).";
                if (from == to) return $"{key}: from and to are the same currency.";
                p.FromCurrency = from.ToString(); p.ToCurrency = to.ToString();   // canonical names

                if (p.FromPerUnit <= 0m) return $"{key}: rate (from per unit of to) must be > 0.";
                if (p.Step <= 0m) return $"{key}: step must be > 0.";
                if (p.MinTo <= 0m) return $"{key}: minimum must be > 0.";
                if (decimal.Remainder(p.MinTo, p.Step) != 0m) return $"{key}: minimum must be a multiple of the step.";
                if (p.MaxToPerTx < 0m || p.DailyCapTo < 0m || p.LifetimeCapTo < 0m || p.MinLevel < 0) return $"{key}: limits can't be negative.";

                // THE WALLET'S SCALE. Every TO quantity and every cost must be exact in decimal(18,4): the step (and so every
                // aligned amount) and the caps are TO amounts; step × rate is the cost QUANTUM — every legal cost is a whole
                // number of it, so if the quantum is representable and at least one wallet unit, every cost is. A rate like
                // 0.000002 Kash per chip is fine WITH a step of 50 chips (quantum 0.0001 Kash); with step 1 it would be a
                // debit MySQL rounds to 0.0000 against a credit that lands — minting.
                if (!Representable(p.Step)) return $"{key}: step must be a whole number of 0.0001 {p.ToCurrency} (the wallet's precision).";
                if (!Representable(p.MinTo) || !Representable(p.MaxToPerTx) || !Representable(p.DailyCapTo) || !Representable(p.LifetimeCapTo))
                    return $"{key}: minimum and caps must be whole numbers of 0.0001 {p.ToCurrency}.";
                var quantum = p.Step * p.FromPerUnit;
                if (quantum < WalletQuantum) return $"{key}: step × rate = {quantum:0.##########} {p.FromCurrency} is below the wallet's precision (0.0001) — the debit would round to nothing. Raise the step or the rate.";
                if (!Representable(quantum)) return $"{key}: step × rate = {quantum:0.##########} {p.FromCurrency} is not a whole number of 0.0001 — choose a step that makes it one.";
                if (p.MaxToPerTx > 0m && p.MaxToPerTx < p.MinTo) return $"{key}: max per exchange is below the minimum.";
                if (p.DailyCapTo > 0m && p.DailyCapTo < p.MinTo) return $"{key}: daily cap is below the minimum — nobody could ever use it.";
                if (p.LifetimeCapTo > 0m && p.LifetimeCapTo < p.MinTo) return $"{key}: lifetime cap is below the minimum.";
                if (p.FromUtc.HasValue && p.ToUtc.HasValue && p.ToUtc <= p.FromUtc) return $"{key}: the window ends before it starts.";

                // One ENABLED pair per route — two rates for the same A→B would make "the rate" ambiguous.
                if (p.Enabled)
                {
                    var route = from + ">" + to;
                    if (byRoute.TryGetValue(route, out var other)) return $"{key}: {from} → {to} is already offered by {other.Key}.";
                    byRoute[route] = p;
                }
            }

            // EVERY cycle must lose, not just A→B→A: with three pairs Chips → Kash → Gems → Chips no pair has a reverse, yet the
            // loop can double chips per lap. Walk every directed cycle in the enabled-pair graph (four currencies at most — the
            // enumeration is tiny) and refuse the first one whose product of rates is ≤ 1 (returns ≥ what went in).
            foreach (var c in Cycles(cfg))
                if (c.Product <= 1m)
                    return $"{string.Join(" → ", c.Pairs.Select(x => x.Key))}: {string.Join(" → ", c.Pairs.Select(x => x.FromCurrency))} → {c.Pairs[0].FromCurrency} would return {c.ReturnFactor:0.####}× — every round trip must lose value.";
            return null;
        }

        /// <summary>One directed cycle in the enabled-pair graph and what a lap returns.</summary>
        public sealed class ExchangeCycle
        {
            public List<ExchangePairDef> Pairs { get; set; } = new List<ExchangePairDef>();
            /// <summary>Product of the rates around the loop (FROM-per-unit-of-TO). A lap returns 1/Product of the start currency.</summary>
            public decimal Product { get; set; }
            public decimal ReturnFactor => Product > 0m ? 1m / Product : 0m;
        }

        /// <summary>
        /// Every simple directed cycle among the ENABLED pairs (each reported once, from its lexicographically smallest currency).
        /// One enabled pair per route (validated), so the graph is a simple digraph on ≤ 4 nodes.
        /// </summary>
        public static List<ExchangeCycle> Cycles(ExchangeCatalogConfig cfg)
        {
            var result = new List<ExchangeCycle>();
            if (cfg?.Pairs == null) return result;
            var edges = cfg.Pairs.Where(p => p != null && p.Enabled && !string.IsNullOrWhiteSpace(p.FromCurrency) && !string.IsNullOrWhiteSpace(p.ToCurrency))
                .GroupBy(p => p.FromCurrency.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var nodes = edges.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var start in nodes)
            {
                var path = new List<ExchangePairDef>();
                void Walk(string at)
                {
                    if (!edges.TryGetValue(at, out var outs)) return;
                    foreach (var e in outs)
                    {
                        var next = e.ToCurrency.Trim();
                        if (string.Equals(next, start, StringComparison.OrdinalIgnoreCase))
                        {
                            var cyc = new List<ExchangePairDef>(path) { e };
                            decimal prod = 1m;
                            foreach (var x in cyc) prod *= x.FromPerUnit;
                            result.Add(new ExchangeCycle { Pairs = cyc, Product = prod });
                            continue;
                        }
                        // Only walk to nodes "after" the start (so each cycle is reported once) and never revisit.
                        if (string.Compare(next, start, StringComparison.OrdinalIgnoreCase) <= 0) continue;
                        if (path.Any(x => string.Equals(x.FromCurrency.Trim(), next, StringComparison.OrdinalIgnoreCase))) continue;
                        path.Add(e);
                        Walk(next);
                        path.RemoveAt(path.Count - 1);
                    }
                }
                Walk(start);
            }
            return result;
        }

        /// <summary>Every cycle, with what a lap returns — information for the admin (all legal only because each loses value).</summary>
        public static List<string> RoundTripNotes(ExchangeCatalogConfig cfg)
            => Cycles(cfg).Select(c => $"{string.Join(" → ", c.Pairs.Select(x => x.Key))}: a lap returns {c.ReturnFactor:0.####}× (must stay < 1).").ToList();
    }
}
