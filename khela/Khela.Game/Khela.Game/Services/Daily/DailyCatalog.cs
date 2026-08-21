using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Khela.Common.Rewards;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Pass;
using Khela.Game.Services.Rewards;

namespace Khela.Game.Services.Daily
{
    /// <summary>One day of the daily ladder. <see cref="Index"/> IS the day — day 7 is node 7.</summary>
    public sealed class DailyNode
    {
        public int Index { get; set; }
        public bool IsMilestone { get; set; }             // UI emphasis only — no payout meaning

        public List<RewardGrant> Rewards { get; set; } = new List<RewardGrant>();

        /// <summary>What the card SAYS — "2.5K", "Mystery". Authored, because a headline is a design decision and not
        /// a sum: a day can pay three things and read as one. Blank falls back to <see cref="PassCatalog.AutoLabel"/>.</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// The daily login config (overlay shape). Admin-editable JSON in Redis <c>khela:daily</c>; falls back to
    /// <see cref="DailyCatalog.Defaults"/>.
    /// </summary>
    public sealed class DailyConfig
    {
        public bool Enabled { get; set; } = true;
        public string Title { get; set; } = "Daily Rewards";

        /// <summary>Rewarded-ad views it costs to buy back ONE missed day. 0 disables ad catch-up entirely.</summary>
        public int AdsPerCatchUp { get; set; } = 1;

        /// <summary>How many missed days may be bought back per run. Bounds both the faucet and the ad-inventory ask;
        /// 0 disables ad catch-up without changing anything else.</summary>
        public int MaxAdCatchUpsPerCycle { get; set; } = 5;

        public List<DailyNode> Nodes { get; set; } = new List<DailyNode>();

        public DailyNode Node(int index) => Nodes?.FirstOrDefault(n => n.Index == index);

        /// <summary>Days in the ladder. The run length IS the node count — there is no separate setting to disagree
        /// with it.</summary>
        public int Days => Nodes?.Count ?? 0;
    }

    /// <summary>What the player may do with their run right now — the whole claim surface in one pure object.</summary>
    public sealed class DailyAvailability
    {
        public int DayIndex { get; set; }
        public int MaxNode { get; set; }

        /// <summary>Days claimable at no cost right now.</summary>
        public List<int> Claimable { get; set; } = new List<int>();

        /// <summary>Missed days the player may buy back with ads right now.</summary>
        public List<int> AdUnlockable { get; set; } = new List<int>();

        /// <summary>Missed and out of reach — the cap is spent, or ad catch-up is off.</summary>
        public List<int> Missed { get; set; } = new List<int>();

        public int AdsPerUnlock { get; set; }
        public int AdUnlocksLeft { get; set; }
    }

    /// <summary>
    /// The daily login catalog: defaults, parsing, the claim rules and the admin-save validation. PURE — no DB, no
    /// Redis, no wallet — so the whole rule set is unit-testable, exactly like <see cref="PassCatalog"/>.
    ///
    /// How it differs from the pass, and why it is its own thing rather than another pass program: the daily ladder is
    /// NOT calendar-bound. It starts the day a player first sees it and runs a fixed number of days, then starts over,
    /// so two players are on different days of the ladder on the same date. It also has ONE track and no subscription,
    /// so none of the golden/entitlement machinery applies. What the two DO share — the reward payload format, the
    /// text form, the local-midnight clock and the ad-credit model — is used from here rather than copied.
    /// </summary>
    public static class DailyCatalog
    {
        /// <summary>The Redis key holding the admin-edited daily config (overlay; absent ⇒ code defaults).</summary>
        public const string RedisKey = "khela:daily";

        /// <summary>Days in the seed ladder. 28 = four weeks of seven, which is how the client lays it out.</summary>
        public const int DefaultLadderLength = 28;

        /// <summary>Hard ceiling on a ladder, so an admin typo can't create a thousand-day run.</summary>
        public const int MaxNodes = 60;

        /// <summary>Sanity ceiling on the ad price of one catch-up.</summary>
        public const int MaxAdsPerCatchUp = 10;

        /// <summary>Ceiling on a card's headline text — it has one line on a small card.</summary>
        public const int MaxCardLabelLength = 32;

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>
        /// The built-in ladder: 28 days that climb, with a bigger beat every 7th day and a chest at the end. Amounts
        /// sit below the pass's free track on purpose — this is the reward for turning up, not for subscribing.
        /// The real ladder is authored in the admin panel, which overrides this wholesale.
        /// </summary>
        public static DailyConfig Defaults() => new DailyConfig
        {
            Enabled = true,
            Title = "Daily Rewards",
            AdsPerCatchUp = 1,
            MaxAdCatchUpsPerCycle = 5,
            Nodes = DefaultLadder(),
        };

        public static List<DailyNode> DefaultLadder()
        {
            var nodes = new List<DailyNode>(DefaultLadderLength);
            for (int i = 1; i <= DefaultLadderLength; i++)
            {
                var node = new DailyNode { Index = i };

                switch (i)
                {
                    case 7:
                        node.IsMilestone = true;
                        node.Rewards = Lines(Chips(2500), Kash(3), Xp(75));
                        break;
                    case 14:
                        node.IsMilestone = true;
                        node.Rewards = Lines(Chips(5000), Kash(5), Xp(120));
                        break;
                    case 21:
                        node.IsMilestone = true;
                        node.Rewards = Lines(Chips(7500), Kash(8), Xp(180));
                        break;
                    case DefaultLadderLength:
                        node.IsMilestone = true;
                        node.Rewards = Lines(RewardGrant.Chest("CK_Chest:Rare"), Kash(15), Xp(300));
                        break;
                    default:
                        // A gentle climb between the milestones, so the ladder has a shape rather than being flat.
                        if (i <= 6) node.Rewards = Lines(Chips(500 + (i - 1) * 100), Xp(25));
                        else if (i <= 13) node.Rewards = Lines(Chips(1200 + (i - 8) * 100), Xp(40));
                        else if (i <= 20) node.Rewards = Lines(Chips(2000 + (i - 15) * 150), Xp(60));
                        else node.Rewards = Lines(Chips(3000 + (i - 22) * 200), Xp(80));
                        break;
                }
                nodes.Add(node);
            }
            return nodes;
        }

        // ---- the claim rules ----

        /// <summary>
        /// What the player may claim, given where they are in the run and what they've already taken.
        ///
        /// PURE and total: today is free, earlier days are missed, and a missed day is buyable with ads while the cap
        /// allows — or free outright while <c>Rewards:BypassAdForMissedDays</c> is on. Nothing here can reach a day the
        /// player's own calendar hasn't arrived at, which is what stops a changed device clock from farming the ladder.
        /// </summary>
        public static DailyAvailability Availability(DailyConfig cfg, int dayIndex, ISet<int> alreadyClaimed,
            int adCatchUpsUsed = 0, bool bypassAdCatchUp = false)
        {
            var a = new DailyAvailability();
            if (cfg == null || cfg.Days == 0) return a;

            a.DayIndex = Math.Max(1, Math.Min(cfg.Days, dayIndex));
            a.MaxNode = a.DayIndex;
            a.AdsPerUnlock = Math.Max(0, cfg.AdsPerCatchUp);

            bool Claimed(int n) => alreadyClaimed != null && alreadyClaimed.Contains(n);
            bool Exists(int n) => cfg.Node(n) != null;

            // Today is always free.
            if (a.MaxNode >= 1 && Exists(a.MaxNode) && !Claimed(a.MaxNode)) a.Claimable.Add(a.MaxNode);

            bool adBackfill = cfg.AdsPerCatchUp > 0 && cfg.MaxAdCatchUpsPerCycle > 0;
            a.AdUnlocksLeft = adBackfill ? Math.Max(0, cfg.MaxAdCatchUpsPerCycle - Math.Max(0, adCatchUpsUsed)) : 0;

            for (int n = 1; n < a.MaxNode; n++)
            {
                if (!Exists(n) || Claimed(n)) continue;

                // The bypass hands missed days over for nothing. It is a testing switch, and it is shared with the
                // pass so a build can never have one ladder charging and the other not.
                if (bypassAdCatchUp) { a.Claimable.Add(n); continue; }

                if (adBackfill && a.AdUnlocksLeft > 0) a.AdUnlockable.Add(n);
                else a.Missed.Add(n);
            }

            a.Claimable.Sort();
            return a;
        }

        // ---- persistence ----

        /// <summary>The effective config: the Redis overlay if it parses, else <see cref="Defaults"/>. A broken
        /// overlay must never take the daily reward down, so a parse failure falls back rather than throwing.</summary>
        public static DailyConfig Parse(string json, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json)) return Defaults();

            try
            {
                var cfg = JsonSerializer.Deserialize<DailyConfig>(json, JsonOptions);
                if (cfg == null) { error = "Config is empty."; return Defaults(); }
                Normalize(cfg);
                return cfg;
            }
            catch (JsonException ex)
            {
                error = $"Parse error: {ex.Message}";
                return Defaults();
            }
        }

        public static string Serialize(DailyConfig cfg) => JsonSerializer.Serialize(cfg, JsonOptions);

        /// <summary>Renumber the ladder 1..N so a hand-edited config can't leave gaps the claim rules would trip on.</summary>
        public static void Normalize(DailyConfig cfg)
        {
            if (cfg?.Nodes == null) return;
            for (int i = 0; i < cfg.Nodes.Count; i++)
            {
                if (cfg.Nodes[i] == null) cfg.Nodes[i] = new DailyNode();
                cfg.Nodes[i].Index = i + 1;
                cfg.Nodes[i].Rewards ??= new List<RewardGrant>();
            }
        }

        /// <summary>
        /// Admin-save validation. Returns null when the config is safe to make live, else the reason, phrased for the
        /// person who typed it. Nothing reaches Redis that the game wouldn't accept.
        /// </summary>
        public static string Validate(DailyConfig cfg)
        {
            if (cfg == null) return "No config.";
            if (cfg.Nodes == null || cfg.Nodes.Count == 0) return "The ladder has no days.";
            if (cfg.Nodes.Count > MaxNodes) return $"A ladder may be at most {MaxNodes} days ({cfg.Nodes.Count} given).";
            if (cfg.AdsPerCatchUp < 0 || cfg.AdsPerCatchUp > MaxAdsPerCatchUp)
                return $"Ads per missed day must be 0–{MaxAdsPerCatchUp}.";
            if (cfg.MaxAdCatchUpsPerCycle < 0 || cfg.MaxAdCatchUpsPerCycle > cfg.Nodes.Count)
                return $"Max ad catch-ups must be 0–{cfg.Nodes.Count}.";

            foreach (var n in cfg.Nodes)
            {
                if (n.Rewards == null || n.Rewards.Count == 0)
                    return $"Day {n.Index}: give it a reward (or shorten the ladder).";

                if (n.Text != null && n.Text.Length > MaxCardLabelLength)
                    return $"Day {n.Index}: the card text is longer than {MaxCardLabelLength} characters.";

                foreach (var line in n.Rewards)
                {
                    if (line == null) return $"Day {n.Index}: empty reward line.";
                    if (line.Amount <= 0m) return $"Day {n.Index}: a reward needs a positive amount.";

                    // The currency allowlist is re-checked at the granter too — this is just the early, friendly no.
                    if (line.Kind == RewardKind.Currency)
                    {
                        if (!RewardCurrencies.TryParse(line.Id, out var currency))
                            return $"Day {n.Index}: '{line.Id}' is not a currency.";
                        if (!RewardCurrencies.IsAllowed(currency))
                            return RewardCurrencies.IsForbidden(currency)
                                ? $"Day {n.Index}: '{currency}' can never be a reward (tradeable token)."
                                : $"Day {n.Index}: '{currency}' is not a permitted reward currency (allowed: {RewardCurrencies.AllowedList}).";
                    }

                    if (line.Kind == RewardKind.Chest && !RewardIds.TryParseChest(line.Id, out _, out _))
                        return $"Day {n.Index}: a chest is \"Key:Tier\", e.g. CK_Chest:Rare.";

                    if (line.Images != null && line.Images.Count > RewardGrant.MaxImages)
                        return $"Day {n.Index}: at most {RewardGrant.MaxImages} images per reward.";
                }
            }
            return null;
        }

        // ---- helpers ----

        private static List<RewardGrant> Lines(params RewardGrant[] lines) => new List<RewardGrant>(lines);
        private static RewardGrant Chips(decimal amount) => RewardGrant.Currency("Chips", amount);
        private static RewardGrant Kash(decimal amount) => RewardGrant.Currency("Kash", amount);
        private static RewardGrant Xp(long amount) => RewardGrant.Xp(amount);
    }
}
