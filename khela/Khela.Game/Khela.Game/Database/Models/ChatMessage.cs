using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Social;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// A 1:1 direct message between two players. Group/table chat is a later addition. Moderation is set
    /// by the future AI moderator (Pending until reviewed). Loose Guid user ids, no EF navigation.
    /// </summary>
    [Table("ChatMessages")]
    [Index(nameof(SenderId), nameof(RecipientId), nameof(SentAt))] // a conversation, in order
    [Index(nameof(RecipientId), nameof(ReadAt))]                   // unread inbox
    public class ChatMessage
    {
        [Key]
        public Guid MessageId { get; set; } = Guid.NewGuid();

        [Required] public Guid SenderId { get; set; }
        [Required] public Guid RecipientId { get; set; }

        [Required, MaxLength(1000)]
        public string Body { get; set; }

        [Required] public DateTime SentAt { get; set; } = DateTime.UtcNow;
        public DateTime? ReadAt { get; set; }

        [Required] public MessageModerationStatus Moderation { get; set; } = MessageModerationStatus.Pending;

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
