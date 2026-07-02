using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    public enum MissionStatus { Active = 0, Completed = 1, Claimed = 2 }

    /// <summary>
    /// One of a player's assigned daily missions for a given UTC date. Server-authoritative: progress is incremented
    /// at settle (idempotent per round) and the chip reward is credited on claim (idempotent). The static def
    /// (target/reward/copy) is looked up from <see cref="Khela.Game.Services.Missions.MissionCatalog"/> by
    /// <see cref="MissionId"/>; only the live state lives here.
    /// </summary>
    [Table("PlayerDailyMissions")]
    [Index(nameof(UserId), nameof(AssignedDate))]                                  // a player's set for a day
    [Index(nameof(UserId), nameof(MissionId), nameof(AssignedDate), IsUnique = true)] // one row per (user, mission, day)
    public class PlayerDailyMission
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }
        [Required, MaxLength(64)] public string MissionId { get; set; }   // MissionDef.Id
        [Required] public DateTime AssignedDate { get; set; }            // UTC date (00:00) the set belongs to

        [Required] public long Progress { get; set; } = 0;
        [Required] public MissionStatus Status { get; set; } = MissionStatus.Active;
        public DateTime? ClaimedAt { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }

    /// <summary>Tracks the "complete ALL daily missions → bundle" claim per (player, UTC date) so it pays once.</summary>
    [Table("PlayerDailyMissionBundles")]
    [Index(nameof(UserId), nameof(AssignedDate), IsUnique = true)]
    public class PlayerDailyMissionBundle
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();
        [Required] public Guid UserId { get; set; }
        [Required] public DateTime AssignedDate { get; set; }
        [Required] public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;
    }
}
