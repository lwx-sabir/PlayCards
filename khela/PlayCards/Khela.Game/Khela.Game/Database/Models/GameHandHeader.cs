using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    public enum GameType
    {
        Blackjack = 1,
        PokerHoldem = 2,
        PokerOmaha = 3,
        Roulette = 4
    }

    public enum HandStatus
    {
        Started = 1,
        Settled = 2,
        Canceled = 3
    }

    [Table("GameHandHeaders")]
    [Index(nameof(TableId))]
    [Index(nameof(GameType))]
    [Index(nameof(StartedAt))]
    [Index(nameof(SettledAt))]
    public class GameHandHeader
    {
        [Key]
        public Guid HandId { get; set; } = Guid.NewGuid();

        [MaxLength(128)]
        public string TableId { get; set; }

        public GameType GameType { get; set; }

        [MaxLength(128)]
        public string RoundId { get; set; }

        public int HandNumber { get; set; }

        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        public DateTime? SettledAt { get; set; }

        public HandStatus Status { get; set; } = HandStatus.Started;

        [MaxLength(128)]
        public string ShoeId { get; set; }

        [MaxLength(256)]
        public string ShuffleSeed { get; set; }

        [MaxLength(256)]
        public string DeckHash { get; set; }

        [MaxLength(256)]
        public string PrevHandHash { get; set; }

        [MaxLength(256)]
        public string ResultChecksum { get; set; }

        public string MetadataJson { get; set; }
    }
}
