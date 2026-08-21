using System;
using System.Collections.Generic;
using System.Globalization;

namespace Khela.Game.Services.Piggy
{
    /// <summary>What fills the bank.</summary>
    public enum PiggyMode
    {
        /// <summary>A share of every chip wagered. The default — see the spec for why loss-based was rejected.</summary>
        Wager = 0,
        /// <summary>A share of a losing round's net loss.</summary>
        Loss = 1,
        /// <summary>Both, added together.</summary>
        Both = 2,
    }

    /// <summary>One rung of the bank ladder: a bigger bank for a higher level, never a faster one.</summary>
    public sealed class PiggyTier
    {
        /// <summary>Lowest player level this tier applies to.</summary>
        public int MinLevel { get; init; }
        public decimal MaxAmount { get; init; }
        /// <summary>The store product that buys it. Empty until IAP products exist.</summary>
        public string PriceSku { get; init; } = "";
    }

    /// <summary>
    /// Piggy-bank knobs. Scalars are overridable live from the <c>khela:settings</c> Redis hash — the same mechanism
    /// the loyalty and progression configs use — so pacing can be tuned without a deploy.
    ///
    /// The rate is a PACING knob, not an economic one: nothing is minted by accruing, so a generous rate costs
    /// nothing. What it buys is how often a player sees a full bank, and therefore how often they see the offer.
    /// </summary>
    public sealed class PiggyConfig
    {
        /// <summary>Ships dark. Turn on once the numbers are tuned and the break exists.</summary>
        public bool Enabled { get; init; } = false;

        public PiggyMode Mode { get; init; } = PiggyMode.Wager;

        /// <summary>Percent of CLEAN (non-gifted) wager banked per settled round.</summary>
        public decimal WagerRatePercent { get; init; } = 50m;

        /// <summary>Percent of a losing round's net loss banked. Only used by <see cref="PiggyMode.Loss"/>/<see cref="PiggyMode.Both"/>.</summary>
        public decimal LossRatePercent { get; init; } = 0m;

        /// <summary>
        /// Ceiling on one day's accrual, as a percent of the bank's capacity — the floor on "slowly".
        ///
        /// Without it the fill time is set by stake size, so a high roller fills the bank in one sitting and the offer
        /// stops being an event. 25 means four days minimum however hard someone plays. 0 = uncapped.
        /// </summary>
        public decimal MaxAccrualPerDayPercent { get; init; } = 25m;

        /// <summary>How full the bank must be before it can be bought. 100 = completely full.</summary>
        public decimal MinBreakPercent { get; init; } = 100m;

        /// <summary>
        /// How much has to have gone into the bank, since the player last saw it celebrated, before the CHIPS FLY on
        /// their return. Below it the bar just fills.
        ///
        /// It exists because a celebration that fires for every trivial amount stops being one. A player back from a
        /// two-hand session should see the bar tick; a player back from an hour should be shown what they earned. The
        /// number is a judgement about what feels like "a session", so it is tunable rather than derived.
        ///
        /// Purely presentational — the accrual itself is identical either way. 0 = always fly.
        /// </summary>
        public decimal MinFlyAmount { get; init; } = 100_000m;

        /// <summary>
        /// How long the player has to buy a full bank once they have SEEN it, in hours. 0 = never expires.
        ///
        /// The clock does not start when the bank fills — it starts when the player is shown the full bank. A window
        /// that ran while they were offline would take away an offer they were never given, and someone who opens the
        /// game to find a piggy that expired last Tuesday learns that filling it is pointless.
        ///
        /// So this is a deadline on the DECISION, not on the play: 72 (three days) or 168 (a week) are both sensible;
        /// the fill rate is paced separately by the wager rate and the daily cap.
        /// </summary>
        public int CycleHours { get; init; } = 72;

        /// <summary>
        /// ⚠️ TESTING — allows a break with no verified purchase. Must be false in production: a free break is the one
        /// thing that turns this feature into a chip faucet. Mirrors <c>Rewards:BypassAdForMissedDays</c>.
        /// </summary>
        public bool BypassPurchase { get; init; } = false;

        /// <summary>
        /// The bank ladder, lowest level first. Calibrated against the low stake tier (bets of 1,000–10,000): a
        /// session of roughly 120,000 handle banks ~60,000 at the default rate, so tier 1 fills in about four
        /// sessions and the daily cap holds the minimum at four days.
        /// </summary>
        public PiggyTier[] Tiers { get; init; } =
        {
            new PiggyTier { MinLevel = 1,  MaxAmount =   250_000m },
            new PiggyTier { MinLevel = 10, MaxAmount =   500_000m },
            new PiggyTier { MinLevel = 25, MaxAmount = 1_000_000m },
            new PiggyTier { MinLevel = 50, MaxAmount = 2_500_000m },
        };

        /// <summary>
        /// Read a tier ladder authored in the admin, as JSON. Anything unusable falls back to
        /// <paramref name="fallback"/> WHOLE — never partially.
        ///
        /// Capacity is the one setting where a bad value doesn't look mistuned, it looks broken: a bank of zero can
        /// never fill and never pays, and the player has no way to tell that from a bug. So a malformed document, an
        /// empty list, or a ladder whose every rung is invalid is refused outright rather than half-applied.
        /// </summary>
        public static PiggyTier[] ParseTiers(string json, PiggyTier[] fallback)
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;

            try
            {
                var raw = System.Text.Json.JsonSerializer.Deserialize<PiggyTier[]>(json, TierJson);
                if (raw == null || raw.Length == 0) return fallback;

                var good = new List<PiggyTier>(raw.Length);
                foreach (var t in raw)
                {
                    if (t == null || t.MaxAmount <= 0m) continue;      // a rung that can never fill is not a rung
                    good.Add(new PiggyTier
                    {
                        MinLevel = t.MinLevel < 1 ? 1 : t.MinLevel,
                        MaxAmount = t.MaxAmount,
                        PriceSku = t.PriceSku ?? "",
                    });
                }

                if (good.Count == 0) return fallback;

                good.Sort((x, y) => x.MinLevel.CompareTo(y.MinLevel));
                return good.ToArray();
            }
            catch { return fallback; }
        }

        /// <summary>The ladder as the admin stores it.</summary>
        public static string SerializeTiers(IEnumerable<PiggyTier> tiers)
            => System.Text.Json.JsonSerializer.Serialize(tiers ?? new List<PiggyTier>(), TierJson);

        private static readonly System.Text.Json.JsonSerializerOptions TierJson = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };

        /// <summary>Apply the Redis overlay — scalars and the tier ladder alike, each falling back to the built-in
        /// default when the stored value is missing or unusable.</summary>
        public static PiggyConfig Overlay(PiggyConfig b, IReadOnlyDictionary<string, string> o)
        {
            if (o == null || o.Count == 0) return b;
            return new PiggyConfig
            {
                Enabled                 = Bool(o, "Piggy:Enabled", b.Enabled),
                Mode                    = Enum(o, "Piggy:Mode", b.Mode),
                WagerRatePercent        = Dec(o, "Piggy:WagerRatePercent", b.WagerRatePercent),
                LossRatePercent         = Dec(o, "Piggy:LossRatePercent", b.LossRatePercent),
                MaxAccrualPerDayPercent = Dec(o, "Piggy:MaxAccrualPerDayPercent", b.MaxAccrualPerDayPercent),
                MinBreakPercent         = Dec(o, "Piggy:MinBreakPercent", b.MinBreakPercent),
                MinFlyAmount            = Dec(o, "Piggy:MinFlyAmount", b.MinFlyAmount),
                CycleHours              = Int(o, "Piggy:CycleHours", b.CycleHours),
                BypassPurchase          = Bool(o, "Piggy:BypassPurchase", b.BypassPurchase),
                Tiers                   = ParseTiers(o.TryGetValue("Piggy:Tiers", out var tiersJson) ? tiersJson : null, b.Tiers),
            };
        }

        private static decimal Dec(IReadOnlyDictionary<string, string> o, string k, decimal d)
            => o.TryGetValue(k, out var v) && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : d;

        private static int Int(IReadOnlyDictionary<string, string> o, string k, int d)
            => o.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : d;

        private static bool Bool(IReadOnlyDictionary<string, string> o, string k, bool d)
            => o.TryGetValue(k, out var v) && bool.TryParse(v, out var x) ? x : d;

        private static PiggyMode Enum(IReadOnlyDictionary<string, string> o, string k, PiggyMode d)
            => o.TryGetValue(k, out var v) && System.Enum.TryParse<PiggyMode>(v, true, out var x) ? x : d;
    }
}
