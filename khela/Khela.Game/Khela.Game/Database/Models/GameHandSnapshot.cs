using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    public enum SnapshotStage
    {
        Deal = 1,
        Settle = 2
    }

    [Table("GameHandSnapshots")]
    [Index(nameof(HandId))]
    [Index(nameof(Stage))]
    public class GameHandSnapshot
    {
        [Key]
        public Guid SnapshotId { get; set; } = Guid.NewGuid();

        [Required]
        public Guid HandId { get; set; }

        public SnapshotStage Stage { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Optional pointer to external blob (e.g., S3) instead of inline JSON.
        /// </summary>
        [MaxLength(512)]
        public string BlobUri { get; set; }

        /// <summary>
        /// Optional inline snapshot (keep small). For large data use BlobUri.
        /// </summary>
        public string SnapshotJson { get; set; }

        [MaxLength(256)]
        public string SnapshotHash { get; set; }
    }
}
