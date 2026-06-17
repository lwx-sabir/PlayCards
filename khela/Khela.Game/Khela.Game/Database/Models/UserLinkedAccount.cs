using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Khela.Common.Profiles;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// A single account linked to a user — an OAuth login (Facebook/Google/Apple), a platform game
    /// service (Google Play Games, Game Center), or a display-only social link (Instagram/TikTok/X...).
    /// One row per (UserId, Provider). Loose Guid UserId, no EF navigation (PlayerWallet convention).
    /// Note: ASP.NET Identity's AspNetUserLogins still owns the actual OAuth login flow; this table is
    /// the profile-facing view (badges, display links) + non-login game-service / social links.
    /// </summary>
    [Table("UserLinkedAccounts")]
    [Index(nameof(UserId), nameof(Provider), IsUnique = true)]         // a user links each provider at most once
    [Index(nameof(Provider), nameof(ProviderUserId), IsUnique = true)] // an external identity maps to one user
    public class UserLinkedAccount
    {
        [Key]
        public Guid LinkedAccountId { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }

        [Required]
        public LinkedAccountProvider Provider { get; set; }

        /// <summary>Provider's stable id (OAuth subject / Play Games player id / Game Center id / vanity handle).</summary>
        [Required, MaxLength(256)]
        public string ProviderUserId { get; set; }

        /// <summary>Display handle or profile URL, e.g. "@user" or "https://instagram.com/user".</summary>
        [MaxLength(512)]
        public string Handle { get; set; }

        /// <summary>True if this provider can authenticate a login; false for display-only social links.</summary>
        [Required]
        public bool IsLoginProvider { get; set; } = false;

        /// <summary>Whether to show this link on the public profile.</summary>
        [Required]
        public bool IsPublic { get; set; } = true;

        [Required]
        public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

        [Timestamp]
        [Column(TypeName = "timestamp(6)")]
        public DateTime? RowVersion { get; set; }
    }
}
