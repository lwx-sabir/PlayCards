using System;
using System.Collections.Generic;

namespace Khela.Common.Rewards
{
    /// <summary>One claimable reward in the player's inbox (GET /api/rewards).</summary>
    public sealed class RewardDto
    {
        public string Id { get; set; }              // Guid as string
        public int Source { get; set; }             // RewardSource as int (0=LevelUp,1=Milestone,2=DailyBonus,3=Pass,4=Achievement,…)
        public string Title { get; set; }
        public int Kind { get; set; }               // RewardKind as int (0=Currency,1=Xp,2=Chest,3=Cosmetic,4=Item)
        public string ItemId { get; set; }          // chest "key:tier" / sku / item key; null for Currency and Xp
        public List<string> Images { get; set; }    // artwork, back layer first (same meaning as RewardGrant.Images)
        public int Currency { get; set; }           // CurrencyType as int (0=Chips,1=Coins,2=Gems,…) — Kind=Currency only
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
        public List<GrantedLineDto> Granted { get; set; } = new List<GrantedLineDto>();  // what was actually paid out (for the collect animation)
        public decimal NewChipBalance { get; set; } // the player's Chips balance after (the common case)
    }
}
