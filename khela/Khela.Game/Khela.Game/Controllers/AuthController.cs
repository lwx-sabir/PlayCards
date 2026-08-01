using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Dtos;
using Khela.Game.Services.Auth;
using Khela.Game.Services.Chat;
using Khela.Game.Services.Wallet;
using Khela.Common.Auth;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly ITokenService _tokenService;
        private readonly JwtSettings _jwtSettings;
        private readonly AppDbContext _dbContext;
        private readonly IWalletService _wallet;
        private readonly IChatModerator _moderator;
        private readonly IFirebaseTokenVerifier _firebase;

        // Stable LoginProvider we store every Firebase-brokered identity under. A Firebase user keeps ONE uid
        // even after linking multiple providers, so one row per account keyed on the uid is all we need.
        private const string FirebaseLoginProvider = "firebase";

        public AuthController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ITokenService tokenService,
            JwtSettings jwtSettings,
            AppDbContext dbContext,
            IWalletService wallet,
            IChatModerator moderator,
            IFirebaseTokenVerifier firebase)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _tokenService = tokenService;
            _jwtSettings = jwtSettings;
            _dbContext = dbContext;
            _wallet = wallet;
            _moderator = moderator;
            _firebase = firebase;
        }

        // ================= Register =================
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var existingEmail = await _userManager.FindByEmailAsync(request.Email);
            if (existingEmail != null)
                return BadRequest(new { message = "Email already exists." });

            var existingUsername = await _userManager.FindByNameAsync(request.Username);
            if (existingUsername != null)
                return BadRequest(new { message = "Username already exists." });

            var user = new ApplicationUser
            {
                UserName = request.Username,
                Email = request.Email,
                CountryCode = request.CountryCode ?? "bd"
            };

            var result = await _userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                var errors = result.Errors.Select(e => e.Description);
                return BadRequest(new { message = "User creation failed.", errors });
            }

            await LinkDeviceToUserAsync(request.DeviceId, user.Id);
             
            // Create the game profile + grant starter chips for the new player.
            await EnsureProfileAndStarterAsync(user);

            // await _userManager.AddToRoleAsync(user, "Player");

            // Generate JWT
            var token = _tokenService.GenerateToken(Guid.Parse(user.Id), user.UserName!);

            var response = new AuthResponse
            {
                Token = token,
                ExpiresIn = _jwtSettings.ExpiryMinutes * 60,
                UserId = user.Id,
                Username = user.UserName!
            };

            return Ok(response);
        }

        // ================= Login =================
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return Unauthorized(new { message = "Invalid credentials." });

            var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, false);
            if (!result.Succeeded)
                return Unauthorized(new { message = "Invalid credentials." });

            await LinkDeviceToUserAsync(request.DeviceId, user.Id);

            // Backfill the game profile + starter for accounts created before bootstrap (idempotent).
            await EnsureProfileAndStarterAsync(user);

            var token = _tokenService.GenerateToken(Guid.Parse(user.Id), user.UserName!);

            var response = new AuthResponse
            {
                Token = token,
                ExpiresIn = _jwtSettings.ExpiryMinutes * 60,
                UserId = user.Id,
                Username = user.UserName!
            };

            return Ok(response);
        }

        // ================= Firebase social sign-in (single broker for Google/Facebook/Apple/guest) =================
        /// <summary>
        /// Verifies a Firebase ID token (any provider) and returns our app JWT. Idempotent find-or-create keyed
        /// on the Firebase uid (stored in AspNetUserLogins). Supports guest -> social UPGRADE: if the caller is
        /// already authenticated (Authorization header) or supplies a linked DeviceId, the social identity is
        /// attached to that existing guest account so their chips carry over — no new account, no lost balance.
        /// </summary>
        [HttpPost("firebase")]
        public async Task<IActionResult> FirebaseSignIn([FromBody] FirebaseAuthRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.IdToken))
                return BadRequest(new { message = "Missing Firebase ID token." });

            if (!_firebase.IsConfigured)
                return StatusCode(503, new { message = "Social sign-in is not configured on the server." });

            ExternalIdentity identity;
            try
            {
                identity = await _firebase.VerifyAsync(request.IdToken, HttpContext.RequestAborted);
            }
            catch (FirebaseAuthException)
            {
                return Unauthorized(new { message = "Invalid or expired sign-in token." });
            }

            // 1) Already linked? -> log into that account.
            var user = await _userManager.FindByLoginAsync(FirebaseLoginProvider, identity.Uid);

            // 2) Not linked yet -> try to UPGRADE an existing guest account (caller's JWT, then DeviceId).
            if (user == null)
            {
                user = await ResolveUpgradeTargetAsync(request.DeviceId);
                if (user != null)
                    await LinkFirebaseAsync(user, identity);   // guest -> social, in place
            }

            // 3) Still nothing -> adopt an existing account by verified email, else create fresh.
            if (user == null)
            {
                if (identity.EmailVerified && !string.IsNullOrWhiteSpace(identity.Email))
                {
                    user = await _userManager.FindByEmailAsync(identity.Email);
                    if (user != null)
                        await LinkFirebaseAsync(user, identity);
                }

                if (user == null)
                {
                    user = await CreateExternalUserAsync(identity, request.CountryCode);
                    if (user == null)
                        return StatusCode(500, new { message = "Account creation failed." });
                }
            }

            // Keep the provider-linked flags current regardless of which path we took.
            await UpdateProviderFlagsAsync(user, identity.Provider);

            // Capture the real email from providers that expose one (Google/Facebook account-picker). Play Games
            // gives no email, so this is a no-op for it. Stored in LinkedEmail — never overwrites the login Email.
            await CaptureLinkedEmailAsync(user, identity);

            await LinkDeviceToUserAsync(request.DeviceId, user.Id);
            await EnsureProfileAndStarterAsync(user);

            var token = _tokenService.GenerateToken(Guid.Parse(user.Id), user.UserName!);
            return Ok(new AuthResponse
            {
                Token = token,
                ExpiresIn = _jwtSettings.ExpiryMinutes * 60,
                UserId = user.Id,
                Username = user.UserName!
            });
        }

        /// <summary>
        /// Finds the existing guest account to upgrade: first the authenticated caller (Sub/NameIdentifier from
        /// their current app JWT), then the account the DeviceId is registered to. Returns null for a brand-new
        /// user, or when the resolved account already carries a Firebase login (don't double-link / hijack).
        /// </summary>
        private async Task<ApplicationUser> ResolveUpgradeTargetAsync(string deviceId)
        {
            var callerId = User?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                           ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);

            ApplicationUser candidate = null;
            if (!string.IsNullOrWhiteSpace(callerId))
                candidate = await _userManager.FindByIdAsync(callerId);

            if (candidate == null && !string.IsNullOrWhiteSpace(deviceId) && Guid.TryParse(deviceId, out var devGuid))
            {
                var device = await _dbContext.DeviceRegistrations.FindAsync(devGuid);
                if (device != null && !string.IsNullOrWhiteSpace(device.UserId))
                    candidate = await _userManager.FindByIdAsync(device.UserId);
            }

            if (candidate == null) return null;

            // Only upgrade an account that isn't already tied to a Firebase identity.
            var logins = await _userManager.GetLoginsAsync(candidate);
            if (logins.Any(l => l.LoginProvider == FirebaseLoginProvider)) return null;

            return candidate;
        }

        private async Task LinkFirebaseAsync(ApplicationUser user, ExternalIdentity identity)
        {
            var info = new UserLoginInfo(FirebaseLoginProvider, identity.Uid, identity.Provider);
            var result = await _userManager.AddLoginAsync(user, info);
            if (!result.Succeeded)
            {
                var errors = string.Join("; ", result.Errors.Select(e => e.Description));
                Console.Error.WriteLine($"[AuthController] AddLogin(firebase) failed for {user.Id}: {errors}");
            }
        }

        private async Task<ApplicationUser> CreateExternalUserAsync(ExternalIdentity identity, string countryCode)
        {
            // Unique, deterministic-from-uid username; email is optional for social accounts, so synthesise a
            // unique placeholder when the provider gives none (RequireUniqueEmail rejects duplicate nulls too).
            var userName = "g_" + identity.Uid.Substring(0, Math.Min(20, identity.Uid.Length));
            var email = !string.IsNullOrWhiteSpace(identity.Email)
                ? identity.Email
                : $"{identity.Uid}@firebase.khela.game";

            var user = new ApplicationUser
            {
                UserName = userName,
                Email = email,
                EmailConfirmed = identity.EmailVerified,
                CountryCode = string.IsNullOrWhiteSpace(countryCode) ? "bd" : countryCode,
                AccountType = (int)AccountType.Player,
                CreateDate = DateTime.UtcNow
            };

            var created = await _userManager.CreateAsync(user);   // no password: external-login-only account
            if (!created.Succeeded)
            {
                var errors = string.Join("; ", created.Errors.Select(e => e.Description));
                Console.Error.WriteLine($"[AuthController] external user creation failed ({identity.Uid}): {errors}");
                return null;
            }

            await LinkFirebaseAsync(user, identity);
            return user;
        }

        /// <summary>
        /// Stores the verified email from a social provider into <see cref="ApplicationUser.LinkedEmail"/> —
        /// the player's real contact email. Deliberately does NOT touch <c>Email</c> (the device-guest login
        /// handle). No-op when the provider gives no verified email (e.g. Play Games).
        /// </summary>
        private async Task CaptureLinkedEmailAsync(ApplicationUser user, ExternalIdentity identity)
        {
            if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Email)) return;
            if (string.Equals(user.LinkedEmail, identity.Email, StringComparison.OrdinalIgnoreCase)) return;

            user.LinkedEmail = identity.Email;
            await _userManager.UpdateAsync(user);
        }

        private async Task UpdateProviderFlagsAsync(ApplicationUser user, string provider)
        {
            // Play Games sign-in (Android's one-tap Google flow) reports "playgames.google.com"; plain Google
            // Sign-In (iOS/web later) reports "google.com". Both mean the account is Google-linked.
            var isGoogle = provider == "google.com" || provider == "playgames.google.com";
            var isFacebook = provider == "facebook.com";
            if (!isGoogle && !isFacebook) return;

            var changed = false;
            if (isGoogle && user.IsGoogleLinked != true) { user.IsGoogleLinked = true; changed = true; }
            if (isFacebook && user.IsFacebookLinked != true) { user.IsFacebookLinked = true; changed = true; }
            if (changed) await _userManager.UpdateAsync(user);
        }

        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
            {
                // Do not reveal that user doesn't exist
                return Ok(new { message = "If an account with that email exists, a reset link has been sent." });
            }

            // Generate password reset token
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);

            // TODO: Send this token to user's email. Example:
            var resetUrl = $"{Request.Scheme}://{Request.Host}/reset-password?email={user.Email}&token={Uri.EscapeDataString(token)}";

            // Use your email service here
            // await _emailService.SendPasswordResetEmail(user.Email, resetUrl);

            return Ok(new { message = "Password reset link sent to your email (simulate in logs for now)." });
        }

        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return BadRequest(new { message = "Invalid request." });

            var result = await _userManager.ResetPasswordAsync(user, request.ResetCode, request.NewPassword);

            if (!result.Succeeded)
            {
                var errors = result.Errors.Select(e => e.Description);
                return BadRequest(new { message = "Password reset failed.", errors });
            }

            return Ok(new { message = "Password has been reset successfully." });
        }

        // ================= Admin/Support Change Password =================
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
                return NotFound(new { message = "User not found." });

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);

            if (!result.Succeeded)
            {
                var errors = result.Errors.Select(e => e.Description);
                return BadRequest(new { message = "Password change failed.", errors });
            }

            return Ok(new { message = "Password changed successfully." });
        }

        /// <summary>
        /// Ensures a new (or pre-bootstrap) user has a game UserProfile + the one-time starter grant.
        /// Idempotent: profile is created once, the wallet grant is keyed on a stable correlation id
        /// (same keys WalletController uses for its lazy grant). Best-effort — never fails auth.
        /// </summary>
        private async Task EnsureProfileAndStarterAsync(ApplicationUser user)
        {
            try
            {
                var userGuid = Guid.Parse(user.Id);
                if (!await _dbContext.UserProfiles.AnyAsync(p => p.UserId == userGuid))
                {
                    var region = (user.CountryCode ?? "").Trim().ToUpperInvariant();
                    if (region.Length != 2) region = "ZZ";
                    var displayName = await SafeDisplayNameAsync(user.UserName!);
                    _dbContext.UserProfiles.Add(new UserProfile
                    {
                        UserId = userGuid,
                        DisplayName = displayName,
                        DisplayNameNormalized = displayName.ToUpperInvariant(),
                        Region = region
                    });
                    await _dbContext.SaveChangesAsync();
                }

                // Starter grant — idempotent on correlation id (same keys as WalletController's lazy grant).
                await _wallet.CreditAsync(user.Id, CurrencyType.Chips, 10000m, TransactionType.Bonus,
                    $"starter:{user.Id}:Chips", new WalletContext { Description = "Starter chips" });
                await _wallet.CreditAsync(user.Id, CurrencyType.Gems, 100m, TransactionType.Bonus,
                    $"starter:{user.Id}:Gems", new WalletContext { Description = "Starter gems" });
            }
            catch (Exception ex)
            {
                // Never fail auth over bootstrap — it's idempotent and re-runs on next login. Log the FULL
                // exception (type + inner SQL error + stack) so a swallowed bootstrap failure is diagnosable.
                Console.Error.WriteLine($"[AuthController] profile/starter bootstrap FAILED for {user.Id}:\n{ex}");
            }
        }

        /// <summary>
        /// Moderates the chosen username at profile creation so offensive/PII names never enter the system or get
        /// broadcast in chat/leaderboards. If it isn't fully clean, fall back to a neutral generated name rather
        /// than failing auth (this bootstrap is best-effort + idempotent).
        /// </summary>
        private async Task<string> SafeDisplayNameAsync(string requested)
        {
            string candidate;
            if (!string.IsNullOrWhiteSpace(requested))
            {
                var mod = await _moderator.ModerateAsync(requested);
                candidate = mod.Outcome == ModerationOutcome.Approved
                    ? (mod.Text.Length <= 32 ? mod.Text : mod.Text.Substring(0, 32))
                    : "Player" + Guid.NewGuid().ToString("N").Substring(0, 6);
            }
            else
            {
                candidate = "Player" + Guid.NewGuid().ToString("N").Substring(0, 6);
            }

            // Enforce the UNIQUE DisplayNameNormalized index BEFORE the insert: if the name (case-folded) is
            // already taken — e.g. two long device/test names truncated to the same 32 chars — append a short
            // discriminator until it's free. Without this the profile INSERT throws a duplicate-key the bootstrap
            // swallows, leaving an account with NO profile (every /api/profile/me then 404s).
            var normalized = candidate.ToUpperInvariant();
            for (int i = 0; i < 6 && await _dbContext.UserProfiles.AnyAsync(p => p.DisplayNameNormalized == normalized); i++)
            {
                var stem = candidate.Length <= 27 ? candidate : candidate.Substring(0, 27);   // keep <= 32 after suffix
                candidate = stem + "_" + Guid.NewGuid().ToString("N").Substring(0, 4);
                normalized = candidate.ToUpperInvariant();
            }
            return candidate;
        }

        private async Task LinkDeviceToUserAsync(string deviceId, string userId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;

            if (!Guid.TryParse(deviceId, out var parsed)) return;

            var device = await _dbContext.DeviceRegistrations.FindAsync(parsed);
            if (device == null) return;

            if (string.IsNullOrWhiteSpace(device.UserId))
            {
                device.UserId = userId;
            }
            device.LastSeen = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }
    }
}
