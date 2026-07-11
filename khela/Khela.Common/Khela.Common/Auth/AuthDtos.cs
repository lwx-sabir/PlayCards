namespace Khela.Common.Auth
{
    public class RegisterRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public string CountryCode { get; set; } = "bd";

        public string DeviceId { get; set; } = string.Empty;
    }

    public class LoginRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public string DeviceId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sign-in / sign-up via any Firebase-brokered provider (Google, Facebook, Apple, guest, ...).
    /// The client obtains a Firebase ID token from the Firebase Auth SDK and posts it here; the server
    /// verifies it and returns the normal <see cref="AuthResponse"/> (our own app JWT).
    ///
    /// Guest -> social upgrade: to link a social identity onto the caller's EXISTING guest account
    /// (keeping their chips), send the current app JWT in the Authorization header AND/OR the DeviceId
    /// here. If neither resolves to an existing account, a fresh account is created.
    /// </summary>
    public class FirebaseAuthRequest
    {
        /// <summary>The Firebase ID token (JWT) from FirebaseUser.TokenAsync on the client.</summary>
        public string IdToken { get; set; } = string.Empty;

        /// <summary>Optional device id (DeviceRegistrations key) — fallback link target for a guest upgrade.</summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>ISO country code for a newly-created account (defaults to "bd").</summary>
        public string CountryCode { get; set; } = "bd";
    }

    public class AuthResponse
    {
        public string Token { get; set; } = string.Empty;
        public int ExpiresIn { get; set; } // seconds
        public string UserId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
    }

    public class ChangePasswordRequest
    {
        public string Email { get; set; } = string.Empty;

        public string NewPassword { get; set; } = string.Empty;
    }

    public class ForgotPasswordRequest
    {
        public string Email { get; set; } = string.Empty;
    }

    public class ResetPasswordRequest
    {
        public string Email { get; set; } = string.Empty;

        public string ResetCode { get; set; } = string.Empty;

        public string NewPassword { get; set; } = string.Empty;
    }
}
