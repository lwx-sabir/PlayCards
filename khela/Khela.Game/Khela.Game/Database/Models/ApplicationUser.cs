using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace Khela.Game.Database.Models
{
    public class ApplicationUser : IdentityUser
    {
        [Required]
        public string CountryCode { get; set; }

        [Required]
        public int AccountType { get; set; } // AccountType enum 

        public string FirstName { get; set; }

        public string LastName { get; set; }

        public string ProfilePicture { get; set; }

        public string Mobile { get; set; }

        public string Addressline1 { get; set; }

        public string Addressline2 { get; set; }

        public string Zip { get; set; }

        public string Place { get; set; }

        public int Country { get; set; }

        public string State { get; set; }

        public string Currency { get; set; }

        public int Language { get; set; }

        public string LangCode { get; set; }    

        public bool? IsFacebookLinked { get; set; } = false;

        public bool? IsGoogleLinked { get; set; } = false;

        /// <summary>
        /// Real email captured from a linked social provider (Google/Facebook account-picker sign-in).
        /// Stored SEPARATELY from <see cref="IdentityUser.Email"/> on purpose: the Identity Email is the
        /// device-guest login handle (derived from the device id), so overwriting it would break a device's
        /// ability to re-login and orphan the account. This is the player's real contact email.
        /// </summary>
        public string LinkedEmail { get; set; }

        public string RefCode { get; set; }

        public DateTime? CreateDate { get; set; }

        public string LastLoginIP { get; set; }

        public string LastLoginCountryCode { get; set; } 

        public DateTime? LastLoginTimestamp { get; set; } 

        public bool? IsActive { get; set; }    

        public string UserRoles { get; set; }

        public DateTime? LastEmailed { get; set; }

        public DateTime? BirthDate { get; set; }

        public int? Gender { get; set; } //Gender Enum
    }

    public enum AccountType
    {
        Player = 1,
        Boss = 2, 
    }

    public enum Gender
    {
        Men = 1,
        Women = 2,
        Other = 3,
        PreferNotToSay = 4
    }
}
