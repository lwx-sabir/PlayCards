using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// One status SEASON (docs/VIP_SPEC.md §2). Status Points are seasonal: they accrue into the <see cref="CurrencyType.Sp"/>
    /// wallet all season, the badge tier is the band that balance reaches, and at the end everyone is reset to a lower tier
    /// (the tier table's <c>ResetTo</c>) and their SP set to that tier's bar.
    ///
    /// There is exactly one <see cref="SeasonStatus.Open"/> season at a time. <see cref="EndsAtUtc"/> is null for a LIFETIME
    /// season (<c>Season:LengthDays</c> = 0) — one that never rolls, which is how the feature ships off.
    ///
    /// Why a table rather than a computed window: a season's boundaries have to survive a change to the length knob. If
    /// "the current season" were derived from <c>now</c> and a length, shortening the season would retroactively move
    /// boundaries that players had already been reset against.
    /// </summary>
    [Table("Seasons")]
    [Index(nameof(Status))]
    [Index(nameof(Index), IsUnique = true)]
    public class Season
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>1, 2, 3 … — the human name of the season and part of every roll's idempotency key.</summary>
        [Required] public int Index { get; set; }

        [Required] public DateTime StartsAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Null = a lifetime season: it never ends and the roll never fires.</summary>
        public DateTime? EndsAtUtc { get; set; }

        [Required] public SeasonStatus Status { get; set; } = SeasonStatus.Open;

        /// <summary>Set when the roll finished for every player, so a resumed roll knows it is done.</summary>
        public DateTime? RolledAtUtc { get; set; }

        /// <summary>How many players the roll reset (audit / the admin panel).</summary>
        [Required] public int PlayersRolled { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }

    /// <summary>Persisted as int — append only.</summary>
    public enum SeasonStatus
    {
        Open = 0,
        Closed = 1,
    }
}
