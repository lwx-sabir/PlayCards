using System;
using System.Collections.Generic;
using Khela.Common.Rewards;

namespace Khela.Common.Daily
{
    /// <summary>
    /// The daily login ladder as the player sees it right now. Everything here is a SERVER decision — which day they
    /// are on, what is claimable, what a missed day costs — so the client renders it and never recomputes it from the
    /// device clock.
    /// </summary>
    public sealed class DailyStateDto
    {
        /// <summary>False when no daily reward is configured or it is switched off. The client shows nothing.</summary>
        public bool Active { get; set; }

        public string Title { get; set; }

        /// <summary>Which run through the ladder this is — it repeats, so a returning player starts over at day 1.</summary>
        public int CycleIndex { get; set; }

        /// <summary>Stable id for this player's current run ("d3"), carried by every claim row.</summary>
        public string CycleKey { get; set; }

        /// <summary>Days in the ladder (30 by default).</summary>
        public int Days { get; set; }

        /// <summary>1-based day of the cycle the player is on, in THEIR calendar.</summary>
        public int DayIndex { get; set; }

        /// <summary>Highest day reachable — never ahead of their calendar.</summary>
        public int MaxNode { get; set; }

        /// <summary>When this player's day next flips (their local midnight), as UTC.</summary>
        public DateTime NextDayUtc { get; set; }

        /// <summary>When the whole ladder restarts, as UTC.</summary>
        public DateTime CycleEndUtc { get; set; }

        /// <summary>The timezone the boundaries were resolved in — shown for support, not used for decisions.</summary>
        public string TimeZoneId { get; set; }

        /// <summary>Verified ad views it costs to buy back ONE missed day. 0 when missed days can't be bought.</summary>
        public int AdsPerUnlock { get; set; }

        /// <summary>Missed days still buyable this cycle, after the per-cycle cap.</summary>
        public int AdUnlocksLeft { get; set; }

        /// <summary>Verified ad credits the player is holding but hasn't spent.</summary>
        public int AdCreditsHeld { get; set; }

        /// <summary>True while <c>Rewards:BypassAdForMissedDays</c> is on — missed days are free. Sent so the client
        /// can drop the ad badge rather than offering a price that isn't charged.</summary>
        public bool AdsBypassed { get; set; }

        public List<DailyNodeDto> Nodes { get; set; } = new List<DailyNodeDto>();
    }

    /// <summary>One day of the daily ladder.</summary>
    public sealed class DailyNodeDto
    {
        /// <summary>Day of the cycle — day 7 is node 7.</summary>
        public int Index { get; set; }

        /// <summary>UI emphasis only; carries no payout meaning.</summary>
        public bool IsMilestone { get; set; }

        public List<RewardGrant> Rewards { get; set; } = new List<RewardGrant>();

        /// <summary>What the card SAYS — authored in the admin panel, else derived. The client prints it verbatim.</summary>
        public string Text { get; set; }

        public bool Claimed { get; set; }

        /// <summary>Claimable right now at no cost.</summary>
        public bool ClaimableNow { get; set; }

        /// <summary>A missed day the player may buy back with rewarded ads.</summary>
        public bool AdUnlockable { get; set; }

        /// <summary>Missed, and out of reach — the ad cap is spent, or catch-up is off.</summary>
        public bool Missed { get; set; }
    }

    /// <summary>What a claim actually paid. <see cref="Granted"/> is the APPLIED amounts, never the advertised ones.</summary>
    public sealed class DailyClaimResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }

        /// <summary>
        /// The day was ALREADY collected — a refusal, but not a failure: the reward is paid and the tile is correct.
        ///
        /// Flagged separately because the client has to tell the two apart. A genuine refusal ("that day hasn't
        /// arrived") must roll the tile back; this one must not, or a duplicate request — a re-tap, a retry after a
        /// timeout, a queue replayed on reconnect — visibly un-collects a day the player really does own.
        /// </summary>
        public bool AlreadyClaimed { get; set; }

        public List<int> ClaimedNodes { get; set; } = new List<int>();
        public List<GrantedLineDto> Granted { get; set; } = new List<GrantedLineDto>();

        /// <summary>Ad credits consumed by this claim.</summary>
        public int AdCreditsSpent { get; set; }

        /// <summary>Chips after the claim, so a HUD can settle without a second round trip.</summary>
        public decimal NewChipBalance { get; set; }
    }

    /// <summary>A single-use token authorising ONE rewarded-ad view for one missed day.</summary>
    public sealed class DailyAdIntentDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }

        /// <summary>Hand this to the ad SDK as its custom data. The CREDIT arrives on the network's server callback.</summary>
        public string Token { get; set; }

        public int Node { get; set; }
        public int AdsRequired { get; set; }
        public int CreditsHeld { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
}
