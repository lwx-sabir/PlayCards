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
        Roulette = 4,
        ThreeCardPoker = 5,
        VideoPoker = 6
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

        /// <summary>Operator (licensee brand) this hand was played under — see <see cref="Tenant"/>. Lets round
        /// history, RTP reporting and dispute lookups be sliced per brand once the engine is licensed out.</summary>
        [Required]
        [MaxLength(Tenant.MaxLength)]
        public string OperatorId { get; set; } = Tenant.Default;

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

        /// <summary>
        /// How many cards had already been dealt from the shoe when THIS hand began. With a multi-deck shoe one
        /// shuffle spans many hands, so the seed alone no longer identifies a hand's cards — replaying it means
        /// rebuilding the shoe from <see cref="ShuffleSeed"/> and skipping this many cards. Always 0 for a
        /// single-deck game, where every hand gets its own shuffle.
        /// </summary>
        public int ShoeCardsDealt { get; set; }

        [MaxLength(256)]
        public string PrevHandHash { get; set; }

        [MaxLength(256)]
        public string ResultChecksum { get; set; }

        public string MetadataJson { get; set; }
    }
}
