namespace Khela.Common.Rewards
{
    /// <summary>
    /// What a reward line pays out. Persisted as <c>int</c> (PlayerRewards.Kind) — <b>APPEND ONLY</b>; never reorder.
    /// Adding a new kind of thing the game can hand out (tickets, clothes, another currency…) is a new value here plus
    /// one <c>IRewardGranter</c> implementation on the server — no change to the systems that AWARD rewards
    /// (pass, missions, chests, level-ups). See docs/PASS_SPEC.md §2.
    /// </summary>
    public enum RewardKind
    {
        Currency = 0,   // wallet currency          Id = "Chips" | "Coins" | "Gems" | "Kash"  (never "Tokens")
        Xp = 1,   // progression XP           Id = null
        Chest = 2,   // roll + credit a chest    Id = "CK_Chest:Rare"  (chest key ':' tier)
        Cosmetic = 3,   // avatar SKU grant         Id = CosmeticSku.SkuId                    [granter not built yet]
        Item = 4,   // generic inventory item   Id = item key, e.g. "lottery_ticket"      [granter not built yet]
    }

    /// <summary>
    /// ONE line of a reward payload — the single currency-agnostic shape every granting system speaks. JSON-friendly
    /// (round-trips through the admin editors) and safe to send to the client, which renders unknown kinds generically
    /// rather than failing.
    /// </summary>
    public sealed class RewardGrant
    {
        public RewardKind Kind { get; set; }

        /// <summary>Identifies WHAT within the kind — meaning depends on <see cref="Kind"/> (see the enum). Null for XP.</summary>
        public string Id { get; set; }

        /// <summary>How much / how many. 1 for a unique item.</summary>
        public decimal Amount { get; set; }

        public static RewardGrant Currency(string currency, decimal amount) => new RewardGrant { Kind = RewardKind.Currency, Id = currency, Amount = amount };
        public static RewardGrant Xp(long amount) => new RewardGrant { Kind = RewardKind.Xp, Amount = amount };
        public static RewardGrant Chest(string keyAndTier) => new RewardGrant { Kind = RewardKind.Chest, Id = keyAndTier, Amount = 1m };

        public override string ToString() => Id == null ? $"{Kind} {Amount}" : $"{Kind} {Id} {Amount}";
    }

    /// <summary>One line as it was ACTUALLY applied (the granter's return value, not the requested value) — what the
    /// client animates. A line the server skipped simply isn't here.</summary>
    public sealed class GrantedLineDto
    {
        public int Kind { get; set; }        // RewardKind as int
        public string Id { get; set; }
        public decimal Amount { get; set; }  // the applied amount (may differ from requested, e.g. a rolled chest)
    }
}
