using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// One player's current run through the daily ladder.
    ///
    /// The daily reward is NOT calendar-bound the way the pass is — it starts the day the player first sees it and
    /// runs a fixed number of days, then starts over. So the cycle can't be derived from the date alone; this row is
    /// the anchor that says which day of which run they are on.
    ///
    /// One row per player, rolled forward in place: <see cref="CycleIndex"/> increments and
    /// <see cref="StartLocalDate"/> moves to the new run's first day. Claims from earlier runs stay behind under their
    /// own cycle key, so history is never rewritten.
    /// </summary>
    [Table("PlayerDailyCycles")]
    [Index(nameof(UserId), IsUnique = true)]
    public class PlayerDailyCycle
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }

        /// <summary>Which run through the ladder this is, from 1. Part of the cycle key.</summary>
        [Required] public int CycleIndex { get; set; } = 1;

        /// <summary>THEIR calendar date this run started — day 1 of the ladder. Date only; the time is meaningless.</summary>
        [Required] public DateTime StartLocalDate { get; set; }

        /// <summary>The timezone the run was anchored in. Kept so a player who travels doesn't silently re-anchor.</summary>
        [MaxLength(64)] public string TimeZoneId { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [Required] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }

    /// <summary>
    /// One claimed day of the daily ladder — the only record of progress, with no summary row to drift out of step.
    ///
    /// The unique (user, cycle, node) index is what makes claiming a day twice structurally impossible, whatever the
    /// device clock claims. Same shape as <see cref="PlayerPassClaim"/> deliberately: the two ladders are audited the
    /// same way and a single query can reconstruct either.
    /// </summary>
    [Table("PlayerDailyClaims")]
    [Index(nameof(UserId), nameof(CycleKey), nameof(Node), IsUnique = true)]   // one day, once
    [Index(nameof(UserId), nameof(CycleKey))]                                   // the ladder read
    public class PlayerDailyClaim
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }

        /// <summary>The run this belongs to ("d3"), from <see cref="PlayerDailyCycle.CycleIndex"/>.</summary>
        [Required, MaxLength(32)] public string CycleKey { get; set; }

        /// <summary>Day of the ladder.</summary>
        [Required] public int Node { get; set; }

        [Required] public DateTime ClaimedOnUtc { get; set; } = DateTime.UtcNow;

        /// <summary>THEIR calendar date when they tapped — later than the day itself for a catch-up claim.</summary>
        [Required] public DateTime ClaimedOnLocalDate { get; set; }

        [MaxLength(64)] public string TimeZoneId { get; set; }

        /// <summary>This missed day was bought back with rewarded ads rather than claimed on the day.</summary>
        [Required] public bool WasAdUnlock { get; set; }

        /// <summary>The payload has been paid out.</summary>
        [Required] public bool Granted { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Set once the payload is granted. A null here after a crash tells the next claim to re-drive the
        /// payout — every granter is idempotent, so re-running pays nothing twice.</summary>
        public DateTime? CompletedAt { get; set; }

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }

    /// <summary>
    /// One VERIFIED rewarded-ad view the player can spend on a missed daily reward.
    ///
    /// Written ONLY by the ad network's signed server-to-server callback — never because a client said it watched
    /// something — and <see cref="AdTransactionId"/> is uniquely indexed so a replayed callback credits nothing.
    /// Credits are scoped to a cycle and stop counting at rollover, so views can't be banked across runs.
    /// </summary>
    [Table("PlayerDailyAdUnlocks")]
    [Index(nameof(AdTransactionId), IsUnique = true)]        // one credit per verified view
    [Index(nameof(UserId), nameof(CycleKey))]                // the per-cycle cap query
    public class PlayerDailyAdUnlock
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }
        [Required, MaxLength(32)] public string CycleKey { get; set; }

        /// <summary>The AD NETWORK's transaction id, from its callback.</summary>
        [Required, MaxLength(128)] public string AdTransactionId { get; set; }

        /// <summary>"admob" | "unityads" | "ironsource".</summary>
        [MaxLength(32)] public string Network { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>The day this credit was spent on; null while unspent.</summary>
        public int? SpentOnNode { get; set; }
        public DateTime? SpentAt { get; set; }
    }
}
