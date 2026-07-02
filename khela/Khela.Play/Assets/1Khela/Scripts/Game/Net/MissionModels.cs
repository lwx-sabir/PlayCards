using System;
using System.Collections.Generic;

namespace PlayCard.Game.Net
{
    /// <summary>Client mirror of the server's MissionDto — one of the player's daily missions.</summary>
    public sealed class MissionData
    {
        public string Id { get; set; }              // claimable instance id (POST /api/missions/{Id}/claim)
        public string MissionId { get; set; }       // catalog id
        public int Type { get; set; }
        public int Difficulty { get; set; }         // 0=Easy,1=Medium,2=Hard
        public string Title { get; set; }
        public string Description { get; set; }
        public string IconKey { get; set; }         // map to a sprite via MissionsPanelBinder's icon list
        public long Progress { get; set; }
        public long Target { get; set; }
        public int Status { get; set; }             // 0=Active,1=Completed,2=Claimed
        public int RewardCurrency { get; set; }     // 0=Chips,1=Coins,2=Gems,3=Tokens,4=Kash
        public decimal RewardAmount { get; set; }

        public bool IsComplete => Progress >= Target;
        public bool IsClaimed => Status == 2;
        public bool IsClaimable => IsComplete && !IsClaimed;
    }

    /// <summary>One rolled reward (currency + amount) — e.g. a line from opening the bundle chest.</summary>
    public sealed class RolledRewardData
    {
        public int Currency { get; set; }   // 0=Chips,1=Coins,2=Gems,3=Tokens,4=Kash
        public decimal Amount { get; set; }
    }

    /// <summary>Client mirror of DailyMissionsDto (GET /api/missions/daily).</summary>
    public sealed class DailyMissionsData
    {
        public List<MissionData> Missions { get; set; } = new List<MissionData>();
        public string BundleChestType { get; set; }   // the complete-all chest (e.g. "CK_Chest")
        public string BundleChestTier { get; set; }   // its tier (e.g. "Common")
        public string BundleChestTitle { get; set; }         // chest title — show in the bundle panel
        public string BundleChestDescription { get; set; }   // chest description — show in the bundle panel
        public bool BundleClaimable { get; set; }
        public bool BundleClaimed { get; set; }
        public DateTime ResetAtUtc { get; set; }     // next UTC midnight — drives the "Resets in HH:MM" countdown
    }

    /// <summary>A result that carries the post-credit chip balance, so the wallet's single source (WalletManager) can
    /// update INSTANTLY from the response — before the reconcile re-pull. Implemented by every claim/reward result.</summary>
    public interface IChipBalanceResult { decimal NewChipBalance { get; } }

    /// <summary>Result of claiming a mission or the bundle. For a bundle claim, <see cref="Rewards"/> holds the chest's
    /// rolled rewards (for the open/reveal animation).</summary>
    public sealed class MissionClaimResultData : IChipBalanceResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public int ClaimedCount { get; set; }
        public decimal NewChipBalance { get; set; }
        public List<RolledRewardData> Rewards { get; set; } = new List<RolledRewardData>();
    }

    // ---- Reward inbox (passive rewards: level-up, gifts…) ----

    /// <summary>Client mirror of the server's RewardDto — one pending claimable reward in the inbox.</summary>
    public sealed class RewardData
    {
        public string Id { get; set; }
        public int Source { get; set; }             // 0=LevelUp,1=Milestone,2=DailyBonus,3=Pass,4=Achievement,5=Gift,6=Admin
        public string Title { get; set; }
        public int Currency { get; set; }
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>Client mirror of the reward-claim ClaimResultDto.</summary>
    public sealed class RewardClaimResultData : IChipBalanceResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public int ClaimedCount { get; set; }
        public decimal NewChipBalance { get; set; }
    }
}
