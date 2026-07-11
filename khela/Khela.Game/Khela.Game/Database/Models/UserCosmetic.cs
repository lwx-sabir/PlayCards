using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// One cosmetic grant — the inventory row proving a user owns a <see cref="CosmeticSku"/>. Unique per
    /// (user, sku) so a purchase can never double-grant; the wallet debit is separately idempotent on the
    /// purchase CorrelationId, so a retried purchase neither double-charges nor double-grants.
    /// </summary>
    [Table("UserCosmetics")]
    [Index(nameof(UserId), nameof(SkuId), IsUnique = true)]
    [Index(nameof(UserId))]
    public class UserCosmetic
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }

        [Required]
        [MaxLength(64)]
        public string SkuId { get; set; }

        /// <summary>How it was obtained: "purchase" | "grant" (admin/reward) | "starter".</summary>
        [Required]
        [MaxLength(32)]
        public string Source { get; set; }

        /// <summary>The purchase idempotency key (same one the wallet debit used) — audit link into the ledger.</summary>
        [MaxLength(64)]
        public string CorrelationId { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
