using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    public enum CurrencyType
    {
        Chips,
        Coins,
        Gems,
        Tokens
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
