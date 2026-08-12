using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// One claimed node of a pass cycle — the ONLY record of pass progress (there is no summary row to drift).
    ///
    /// <see cref="Node"/> is the day of the cycle, so the unique (user, pass, cycle, node) index makes claiming a day
    /// twice structurally impossible, no matter what timezone the player's clock claims to be in.
    /// See docs/PASS_SPEC.md §4.
    /// </summary>
    [Table("PlayerPassClaims")]
    [Index(nameof(UserId), nameof(PassKey), nameof(CycleKey), nameof(Node), IsUnique = true)]   // one node, once
    [Index(nameof(UserId), nameof(PassKey), nameof(CycleKey))]                                  // the ladder read
    public class PlayerPassClaim
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }

        /// <summary>The pass PROGRAM ("monthly"; a Season Pass is another key).</summary>
        [Required, MaxLength(32)] public string PassKey { get; set; }

        /// <summary>The cycle ("2026-09" for a monthly pass — the player's LOCAL month).</summary>
        [Required, MaxLength(32)] public string CycleKey { get; set; }

        /// <summary>Which day of the cycle this is.</summary>
        [Required] public int Node { get; set; }

        /// <summary>When the player actually tapped.</summary>
        [Required] public DateTime ClaimedOnUtc { get; set; } = DateTime.UtcNow;

        /// <summary>THEIR calendar date when they tapped — later than the node's own day for a catch-up claim.
        /// Kept so support can reconstruct exactly what the player was shown.</summary>
        [Required] public DateTime ClaimedOnLocalDate { get; set; }

        /// <summary>The timezone that decided the day boundary for this claim.</summary>
        [MaxLength(64)] public string TimeZoneId { get; set; }

        /// <summary>This missed day was bought back with rewarded ads rather than claimed on the day.</summary>
        [Required] public bool WasAdUnlock { get; set; }

        /// <summary>The free payload has been paid out.</summary>
        [Required] public bool FreeGranted { get; set; }

        /// <summary>The golden payload has been paid out. False on a claim made without an entitlement — which is
        /// exactly what the missed-days unlock looks for when a subscription later starts.</summary>
        [Required] public bool GoldenGranted { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Set once every payload for this node has been granted. A null here after a crash is the signal
        /// for the next claim to re-run the ungranted parts (each granter is idempotent).</summary>
        public DateTime? CompletedAt { get; set; }

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }

    /// <summary>
    /// One purchased window of the golden track. Golden is an auto-renewing REAL-MONEY subscription bought on any day,
    /// so its period (say the 20th → the 20th) crosses cycle boundaries: this is a window, never a per-cycle flag.
    /// The first purchase and every renewal append their own row sharing an <see cref="OriginalTransactionId"/>, so
    /// billing history stays auditable and a renewal can never silently shorten an existing entitlement.
    /// </summary>
    [Table("PlayerPassEntitlements")]
    [Index(nameof(UserId), nameof(PassKey), nameof(PurchaseRef), IsUnique = true)]   // one row per store transaction
    [Index(nameof(UserId), nameof(PassKey))]
    public class PlayerPassEntitlement
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }
        [Required, MaxLength(32)] public string PassKey { get; set; }

        /// <summary>"iap" | "admin" | "gift".</summary>
        [Required, MaxLength(32)] public string Source { get; set; }

        /// <summary>The store transaction id (admin grants use "admin:{guid}"). Uniquely indexed per user+pass, so a
        /// replayed receipt grants nothing new.</summary>
        [Required, MaxLength(96)] public string PurchaseRef { get; set; }

        /// <summary>The SUBSCRIPTION's id — every renewal of the same subscription shares it.</summary>
        [MaxLength(96)] public string OriginalTransactionId { get; set; }

        [Required] public DateTime StartsAt { get; set; }

        /// <summary>From the STORE's period end — never computed from our own clock or the cycle.</summary>
        [Required] public DateTime ExpiresAt { get; set; }

        public bool AutoRenew { get; set; }

        /// <summary>Refund / chargeback / admin revoke. Collected rewards are never clawed back.</summary>
        public DateTime? RevokedAt { get; set; }

        [Required] public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }

    /// <summary>
    /// One VERIFIED rewarded-ad view a player can spend on catching up a missed day.
    ///
    /// Rows are written ONLY by the ad network's signed server-to-server callback — never by the client claiming it
    /// watched something — and <see cref="AdTransactionId"/> is uniquely indexed so a replayed callback credits
    /// nothing. Credits are scoped to a cycle and simply stop counting at rollover, so views can't be banked.
    /// See docs/PASS_SPEC.md §5.6.
    /// </summary>
    [Table("PlayerPassAdUnlocks")]
    [Index(nameof(AdTransactionId), IsUnique = true)]              // one credit per verified view
    [Index(nameof(UserId), nameof(PassKey), nameof(CycleKey))]     // the per-cycle cap query
    public class PlayerPassAdUnlock
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }
        [Required, MaxLength(32)] public string PassKey { get; set; }
        [Required, MaxLength(32)] public string CycleKey { get; set; }

        /// <summary>The AD NETWORK's transaction id, from its callback.</summary>
        [Required, MaxLength(128)] public string AdTransactionId { get; set; }

        /// <summary>"admob" | "unityads" | "ironsource".</summary>
        [MaxLength(32)] public string Network { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>The node this credit was spent on; null while unspent.</summary>
        public int? SpentOnNode { get; set; }
        public DateTime? SpentAt { get; set; }
    }
}
