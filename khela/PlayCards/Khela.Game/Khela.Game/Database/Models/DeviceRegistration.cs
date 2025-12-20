using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Khela.Game.Database.Models
{
    [Table("DeviceRegistrations")]
    public class DeviceRegistration
    {
        [Key]
        public Guid DeviceId { get; set; } = Guid.NewGuid();

        [MaxLength(256)]
        public string Fingerprint { get; set; }

        [MaxLength(128)]
        public string AppSetId { get; set; }

        public string UserId { get; set; }

        [MaxLength(32)]
        public string GameVersion { get; set; }

        [MaxLength(64)]
        public string TimeZone { get; set; }

        [MaxLength(64)]
        public string LastIp { get; set; }

        public DateTime LastSeen { get; set; } = DateTime.UtcNow;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
