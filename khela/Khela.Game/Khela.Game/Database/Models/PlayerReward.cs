using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Rewards;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>What produced a claimable reward (for grouping + icon in the inbox). Extend as new sources ship.</summary>
    public enum RewardSource { LevelUp = 0, Milestone = 1, DailyBonus = 2, Pass = 3, Achievement = 4, Gift = 5, Admin = 6, Other = 99 }

    /// <summary>Lifecycle of a claimable reward.</summary>
    public enum RewardStatus { Pending = 0, Claimed = 1, Expired = 2 }

    /// <summary>
    /// A claimable reward in a player's inbox — produced by some system (level-up, daily bonus, pass, achievement…)
    /// and COLLECTED by the player tapping. The granting system only ENQUEUES this row (Pending); the wallet is
    /// credited ONLY on claim (<see cref="Khela.Game.Services.Rewards.IRewardService"/>), idempotent on this row's id
    /// so a double-tap can never double-pay. This keeps auto-faucets OUT of the wallet — chips arrive only when the
    /// player collects, exactly like daily/pass rewards.
    /// </summary>
    [Table("PlayerRewards")]
    [Index(nameof(UserId), nameof(Status))]              // the inbox query (pending per user)
    [Index(nameof(IdempotencyKey), IsUnique = true)]     // a given grant (e.g. a specific level-up) enqueues exactly once
    public class PlayerReward
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }
        [Required] public RewardSource Source { get; set; }

        /// <summary>WHAT this reward pays out. Defaults to <see cref="RewardKind.Currency"/>, so every pre-existing row
        /// keeps its exact meaning (<see cref="Currency"/> + <see cref="Amount"/>) and no backfill is needed. Non-currency
        /// kinds carry their target in <see cref="ItemId"/>; XP/Chest/Item ignore <see cref="Currency"/>.</summary>
        [Required] public RewardKind Kind { get; set; } = RewardKind.Currency;

        /// <summary>The target within <see cref="Kind"/> — chest "key:tier", cosmetic SkuId, item key. Null for
        /// Currency (which uses <see cref="Currency"/>) and XP.</summary>
        [MaxLength(64)] public string ItemId { get; set; }

        [Required] public CurrencyType Currency { get; set; } = CurrencyType.Chips;
        [Precision(18, 4)] public decimal Amount { get; set; }
        [Required] public RewardStatus Status { get; set; } = RewardStatus.Pending;

        [MaxLength(96)] public string Title { get; set; }        // display, e.g. "Level 12 reward"
        public string MetadataJson { get; set; }                 // optional structured payload (level, day index, pass tier…)

        /// <summary>Stable key from the granting system (e.g. "xp:lvlup:{user}:{level}") — the unique index makes a
        /// given grant enqueue exactly once even under retries/concurrency.</summary>
        [Required, MaxLength(160)] public string IdempotencyKey { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ClaimedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }                 // optional (e.g. a daily reward that lapses)

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }
}
