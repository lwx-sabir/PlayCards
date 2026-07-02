using System.Collections.Generic;

namespace Khela.Common.Chests
{
    /// <summary>One rolled reward line from opening a chest.</summary>
    public sealed class RolledRewardDto
    {
        public int Currency { get; set; }       // CurrencyType as int (0=Chips,1=Coins,2=Gems,3=Tokens,4=Kash)
        public decimal Amount { get; set; }
    }

    /// <summary>A chest's identity + display text (for editor pickers / UI). No reward ranges.</summary>
    public sealed class ChestInfoDto
    {
        public string Key { get; set; }
        public string Tier { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
    }

    /// <summary>Result of opening a chest — the actual rolled rewards (for the open animation).</summary>
    public sealed class ChestOpenResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string ChestType { get; set; }   // the chest Key (e.g. "CK_Chest")
        public string Tier { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string IconKey { get; set; }
        public List<RolledRewardDto> Rewards { get; set; } = new List<RolledRewardDto>();
        public decimal NewChipBalance { get; set; }
    }
}
