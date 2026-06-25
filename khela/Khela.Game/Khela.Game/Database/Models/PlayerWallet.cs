using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// Wallet currencies. Persisted as <c>int</c> — <b>append new values only; never reorder or insert</b>
    /// (that would renumber existing wallet rows). Only <see cref="Chips"/> and <see cref="Coins"/> are
    /// wagerable (enforced in <c>WalletService.IsWagerableCurrency</c>); every other value here is
    /// non-wagerable by construction and can never be bet or won at a table.
    /// </summary>
    public enum CurrencyType
    {
        Chips,   // 0 — wagerable play money (non-cashable)
        Coins,   // 1 — wagerable play money (non-cashable)
        Gems,    // 2 — premium soft currency (non-wagerable)
        Tokens,  // 3 — Phase-2 revenue-backed tradeable token (never wagered, never won)
        Kash     // 4 — cosmetics & gifting spend currency (non-wagerable; buys items/gifts, never bet)
    }

    [Table("PlayerWallets")]
    [Index(nameof(UserId), nameof(Currency), IsUnique = true)]
    public class PlayerWallet
    {
        [Key]
        public Guid WalletId { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }  // FK to Users table

        [Required]
        public CurrencyType Currency { get; set; }

        [Precision(18, 4)]
        [Required]
        public decimal Balance { get; set; } = 0m;

        [Precision(18, 4)]
        public decimal PendingBalance { get; set; } = 0m;  // Optional for in-progress bets

        // Tainted portion of Balance: chips received from another PLAYER's gift (Progression Spec §6).
        // The clean/earned balance is the remainder (Balance - GiftedBalance) and is what accrues progression.
        // INVARIANT: 0 <= GiftedBalance <= Balance, maintained on every WalletService.ApplyAsync. Bets spend
        // EARNED first; a winning payout credits back the same gifted fraction as its stake, so gifted chips
        // stay tainted through wins (they can only leave by being wagered-and-lost or spent). Defaults 0, so
        // every existing chip is automatically earned — no backfill needed.
        [Precision(18, 4)]
        [Required]
        public decimal GiftedBalance { get; set; } = 0m;

        [Required]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Concurrency token. On MySQL/Pomelo this maps to a `timestamp(6)` rowversion
        // column (as created by the 'audit' migration). It must stay DateTime? to match
        // the migrated schema and snapshot — a byte[] rowversion is not supported on MySQL.
        // Pin the column type: newer Pomelo defaults [Timestamp] DateTime to datetime(6),
        // which silently drifts the model off the migrated timestamp(6) schema.
        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }

        public bool IsLocked { get; set; } = false;
    }
}
