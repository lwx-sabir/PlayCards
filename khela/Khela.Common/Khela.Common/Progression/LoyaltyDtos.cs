using System.Collections.Generic;

namespace Khela.Common.Progression
{
    /// <summary>One Loyalty-Store item for the client (Progression Spec §4).</summary>
    public sealed class LoyaltyStoreItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = "chips";
        public long CostLp { get; set; }
        public decimal ChipAmount { get; set; }
        public int MinVipTier { get; set; }
        public bool Affordable { get; set; }   // balance >= CostLp
        public bool Unlocked { get; set; }     // player's VIP tier >= MinVipTier
    }

    /// <summary>The Loyalty store + the caller's balance (GET /api/loyalty).</summary>
    public sealed class LoyaltyStoreDto
    {
        public long Points { get; set; }              // current redeemable LP balance
        public long LifetimePoints { get; set; }
        public List<LoyaltyStoreItemDto> Items { get; set; } = new List<LoyaltyStoreItemDto>();
    }

    /// <summary>Result of POST /api/loyalty/redeem.</summary>
    public sealed class RedeemResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }      // null on success; a short reason otherwise
        public string ItemId { get; set; }
        public long Points { get; set; }        // new LP balance after the redeem
        public decimal ChipAmount { get; set; } // chips granted (kind=chips)
    }
}
