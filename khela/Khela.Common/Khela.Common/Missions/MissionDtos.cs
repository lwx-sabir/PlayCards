using System;
using System.Collections.Generic;
using Khela.Common.Chests;

namespace Khela.Common.Missions
{
    /// <summary>One of the player's daily missions (live state + the def's display/target/reward).</summary>
    public sealed class MissionDto
    {
        public string Id { get; set; }              // the claimable instance id (PlayerDailyMission.Id)
        public string MissionId { get; set; }       // catalog id
        public int Type { get; set; }               // MissionType as int
        public int Difficulty { get; set; }         // 0=Easy,1=Medium,2=Hard
        public string Title { get; set; }
        public string Description { get; set; }
        public string IconKey { get; set; }
        public long Progress { get; set; }
        public long Target { get; set; }
        public int Status { get; set; }             // 0=Active,1=Completed,2=Claimed
        public int RewardCurrency { get; set; }     // CurrencyType as int
        public decimal RewardAmount { get; set; }
    }

    /// <summary>GET /api/missions/daily — the player's daily missions + the complete-all CHEST + reset time.</summary>
    public sealed class DailyMissionsDto
    {
        public List<MissionDto> Missions { get; set; } = new List<MissionDto>();
        public string BundleChestType { get; set; }   // the chest granted for completing all (e.g. "CK_Chest")
        public string BundleChestTier { get; set; }   // its tier (e.g. "Common")
        public string BundleChestTitle { get; set; }         // resolved from the chest catalog — shown in the bundle panel
        public string BundleChestDescription { get; set; }   // resolved from the chest catalog
        public bool BundleClaimable { get; set; }   // every mission completed and the bundle not yet claimed
        public bool BundleClaimed { get; set; }
        public DateTime ResetAtUtc { get; set; }     // next UTC midnight — client shows the "Resets in HH:MM" countdown
    }

    /// <summary>Result of claiming a mission or the bundle.</summary>
    public sealed class MissionClaimResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public int ClaimedCount { get; set; }        // missions/bundle claimed this call
        public decimal NewChipBalance { get; set; }  // chips after (the common reward currency)
        public List<RolledRewardDto> Rewards { get; set; } = new List<RolledRewardDto>();   // bundle claim: the chest's rolled rewards
    }
}
