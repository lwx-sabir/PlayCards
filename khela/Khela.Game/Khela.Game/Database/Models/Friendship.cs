using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Social;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// A friend-graph edge. One row per friend REQUEST (Requester -> Addressee); an Accepted row IS the
    /// friendship (query both directions). Loose Guid user ids, no EF navigation (PlayerWallet convention).
    /// The service checks the reverse pair before inserting so A&lt;-&gt;B can't be duplicated.
    /// </summary>
    [Table("Friendships")]
    [Index(nameof(RequesterId), nameof(AddresseeId), IsUnique = true)] // one edge per ordered pair
    [Index(nameof(AddresseeId), nameof(Status))]                       // incoming requests / "friends of me"
    [Index(nameof(RequesterId), nameof(Status))]                       // outgoing requests / "my friends"
    public class Friendship
    {
        [Key]
        public Guid FriendshipId { get; set; } = Guid.NewGuid();

        [Required] public Guid RequesterId { get; set; } // who sent the request
        [Required] public Guid AddresseeId { get; set; } // who received it

        [Required] public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RespondedAt { get; set; }

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
