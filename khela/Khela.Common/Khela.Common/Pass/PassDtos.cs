using System;
using System.Collections.Generic;
using Khela.Common.Rewards;

namespace Khela.Common.Pass
{
    /// <summary>One node of the ladder as the client renders it.</summary>
    public sealed class PassNodeDto
    {
        public int Index { get; set; }                 // = day of the cycle
        public bool IsMilestone { get; set; }
        public List<RewardGrant> Free { get; set; } = new List<RewardGrant>();
        public List<RewardGrant> Golden { get; set; } = new List<RewardGrant>();

        public bool Claimed { get; set; }              // the free payload has been collected
        public bool GoldenClaimed { get; set; }        // the golden payload has been collected
        public bool ClaimableNow { get; set; }         // tappable right now at no cost
        public bool AdUnlockable { get; set; }         // a missed day this player may buy back with ads
        public bool GoldenLocked { get; set; }         // a missed day only the subscription reaches
    }

    /// <summary>The whole pass screen in one call (GET /api/pass).</summary>
    public sealed class PassStateDto
    {
        /// <summary>False when no program/cycle is live — the client simply hides the pass. Everything else is unset.</summary>
        public bool Active { get; set; }
        public string Error { get; set; }

        public string PassKey { get; set; }
        public string CycleKey { get; set; }
        public string Title { get; set; }

        public DateTime CycleStartUtc { get; set; }
        public DateTime CycleEndUtc { get; set; }
        /// <summary>When the player's OWN day flips — what the client counts down to.</summary>
        public DateTime NextDayUtc { get; set; }
        public string TimeZoneId { get; set; }

        public int DayIndex { get; set; }              // 1-based day of the cycle, in the player's calendar
        public int Days { get; set; }                  // days in this cycle (28..31)
        public int MaxNode { get; set; }               // highest node reachable today

        public bool IsGolden { get; set; }
        public DateTime? GoldenUntilUtc { get; set; }
        public bool AutoRenew { get; set; }
        public string GoldenProductIdApple { get; set; }
        public string GoldenProductIdGoogle { get; set; }
        public decimal GoldenPriceUsd { get; set; }    // display fallback; the store's localized price wins

        public string CatchUp { get; set; }            // CatchUpPolicy name
        public int AdsPerUnlock { get; set; }
        public int AdUnlocksLeft { get; set; }
        public int AdCreditsHeld { get; set; }         // verified, unspent views
        /// <summary>Missed days only a subscription reaches — the "unlock N missed days" CTA number.</summary>
        public int GoldenLockedCount { get; set; }

        public List<PassNodeDto> Nodes { get; set; } = new List<PassNodeDto>();
    }

    /// <summary>Result of claiming one or more nodes.</summary>
    public sealed class PassClaimResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public List<int> ClaimedNodes { get; set; } = new List<int>();
        /// <summary>What was ACTUALLY paid out (the granters' applied amounts) — what the client animates.</summary>
        public List<GrantedLineDto> Granted { get; set; } = new List<GrantedLineDto>();
        public int AdCreditsSpent { get; set; }
        public decimal NewChipBalance { get; set; }
    }

    /// <summary>Result of a golden grant (purchase, renewal, or admin comp).</summary>
    public sealed class PassPurchaseResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public bool IsGolden { get; set; }
        public DateTime? GoldenUntilUtc { get; set; }
        /// <summary>Missed days unlocked by this purchase — enqueued to the reward inbox, collected by tapping.</summary>
        public int UnlockedNodes { get; set; }
    }
}
