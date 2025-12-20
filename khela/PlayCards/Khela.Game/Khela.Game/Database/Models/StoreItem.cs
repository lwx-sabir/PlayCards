using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace Khela.Game.Database.Models
{
    public enum StoreItemType
    {
        Chips,
        Coins,
        Gems,
        Lottery
    }

    [Table("StoreItems")]
    public class StoreItem
    {
        [Key]
        public Guid ItemId { get; set; } = Guid.NewGuid();

        [Required]
        [MaxLength(100)]
        public string Name { get; set; }

        [MaxLength(500)]
        public string Description { get; set; } 

        [Column(TypeName = "decimal(18,2)")]
        [Required]
        public decimal Price { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        [Required]
        public decimal Quantity { get; set; }

        [Required]
        public StoreItemType ItemType { get; set; }

        public string MetadataJson { get; set; }   

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
