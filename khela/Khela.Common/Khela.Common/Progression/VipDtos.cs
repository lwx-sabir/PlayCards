namespace Khela.Common.Progression
{
    /// <summary>
    /// The caller's VIP status for the profile / VIP screen (GET /api/vip/me). Tier is computed live from the
    /// trailing-window Status Points (+ the spend floor on upper bands); the badge shows only when LIT (recent
    /// activity within the badge window) and not hidden by the player. The benefit multiplier boosts the
    /// Loyalty/store track only — never Status Points.
    /// </summary>
    public sealed class VipStatusDto
    {
        public int Tier { get; set; }                   // VipTier as int (0=None … 1=Bronze floor … 7=Black Diamond)
        public string TierName { get; set; } = string.Empty;
        public bool HasBadge { get; set; }              // Silver+ (Bronze/None carry no badge)
        public bool BadgeLit { get; set; }              // active within the badge window (cosmetic)
        public bool HideBadge { get; set; }             // player opt-out (hide the badge from others)
        public long StatusPoints { get; set; }          // trailing-window SP — the RANK basis
        public long LifetimeStatusPoints { get; set; }
        public decimal BenefitMultiplier { get; set; }  // Loyalty/store boost at this tier (never applied to SP)
        public int? NextTier { get; set; }              // null at the top
        public string NextTierName { get; set; }
        public long SpToNextTier { get; set; }          // remaining SP to the next band (0 at top)
        public decimal SpendToNextTierUsd { get; set; } // remaining trailing spend to the next band (0 if none / n/a)

        // VIP Level (§3.6) — the premium ladder on top of the tier. BenefitMultiplier above already includes its bonus.
        public int VipLevel { get; set; }                       // 0 = none; 1..10
        public long VipLevelProgress { get; set; }              // into-next-level grind progress
        public long VipLevelProgressToNext { get; set; }        // remaining grind to the next VIP level (0 at VIP 10)
        public System.DateTime? VipLevelMaintainedThrough { get; set; }   // level holds until this; else the monthly review drops 1
    }

    /// <summary>Result of POST /api/vip/maintain (spend Loyalty Points to hold the current VIP level).</summary>
    public sealed class VipMaintainResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }                       // null on success
        public bool AlreadyMaintained { get; set; }             // true if it was already maintained (no charge)
        public long LoyaltyPoints { get; set; }                 // LP balance after
        public int VipLevel { get; set; }
        public System.DateTime? MaintainedThrough { get; set; }
    }
}
