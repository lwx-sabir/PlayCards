using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    public enum HandActionType
    {
        Bet = 1,
        Deal = 2,
        Hit = 3,
        Stand = 4,
        Double = 5,
        Split = 6,
        Insurance = 7,
        DealerPlay = 8,
        Surrender = 9,
        Fold = 10,
        Check = 11,
        Call = 12,
        Raise = 13
    }

    [Table("GameHandActions")]
    [Index(nameof(HandId))]
    [Index(nameof(UserId))]
    public class GameHandAction
    {
        [Key]
        public Guid ActionId { get; set; } = Guid.NewGuid();

        [Required]
        public Guid HandId { get; set; }

        public Guid? UserId { get; set; }

        public int? SeatNumber { get; set; }

        public HandActionType ActionType { get; set; }

        [MaxLength(64)]
        public string CardDrawn { get; set; } // e.g., "AH"

        public int? HandValueAfter { get; set; }

        [Precision(18, 4)]
        public decimal? Amount { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public string MetadataJson { get; set; }
    }
}
