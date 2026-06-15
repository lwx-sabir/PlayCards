using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// A named, time-bounded season — the durable source of truth for Period == Season leaderboards.
    /// One GLOBAL season timeline ("Season 7" spans all games); the active season's <see cref="SeasonKey"/>
    /// ("S7") becomes the PeriodKey for every Season board. A per-game timeline can be added later by
    /// appending a GameType column (non-breaking). The seal job rolls IsActive from n to n+1 at EndsAtUtc.
    /// </summary>
    [Table("LeaderboardSeasons")]
    [Index(nameof(SeasonKey), IsUnique = true)]
    [Index(nameof(SeasonNumber), IsUnique = true)]
    [Index(nameof(IsActive))]
    public class LeaderboardSeason
    {
        [Key]
        public Guid SeasonId { get; set; } = Guid.NewGuid();

        /// <summary>Monotonic season ordinal (7 => "S7"). Unique.</summary>
        [Required]
        public int SeasonNumber { get; set; }

        /// <summary>Stable key used in the PeriodKey + Redis keys, e.g. "S7". Unique.</summary>
        [Required, MaxLength(16)]
        public string SeasonKey { get; set; }

        [Required, MaxLength(64)]
        public string Name { get; set; }

        [Required] public DateTime StartsAtUtc { get; set; }
        [Required] public DateTime EndsAtUtc   { get; set; }

        /// <summary>Exactly one season is active at a time (enforced by the season/seal service, not the DB).</summary>
        [Required] public bool IsActive { get; set; } = false;

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [Required] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
