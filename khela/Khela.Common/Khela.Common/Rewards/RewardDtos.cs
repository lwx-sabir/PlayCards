using System;

namespace Khela.Common.Rewards
{
    /// <summary>One claimable reward in the player's inbox (GET /api/rewards).</summary>
    public sealed class RewardDto
    {
        public string Id { get; set; }              // Guid as string
        public int Source { get; set; }             // RewardSource as int (0=LevelUp,1=Milestone,2=DailyBonus,3=Pass,4=Achievement,…)
        public string Title { get; set; }
        public int Currency { get; set; }           // CurrencyType as int (0=Chips,1=Coins,2=Gems,…)
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>Result of a claim (single or claim-all).</summary>
    public sealed class ClaimResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }           // null on success
        public int ClaimedCount { get; set; }       // rewards actually claimed this call (0 if already claimed)
        public decimal NewChipBalance { get; set; } // the player's Chips balance after (the common case)
    }
}
