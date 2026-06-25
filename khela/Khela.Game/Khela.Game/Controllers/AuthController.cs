using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Dtos;
using Khela.Game.Services.Chat;
using Khela.Game.Services.Wallet;
using Khela.Common.Auth;
using Microsoft.AspNetCore.Identity; 
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

        public AuthController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ITokenService tokenService,
            JwtSettings jwtSettings,
            AppDbContext dbContext,
            IWalletService wallet,
            IChatModerator moderator)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _tokenService = tokenService;
            _jwtSettings = jwtSettings;
            _dbContext = dbContext;
            _wallet = wallet;
            _moderator = moderator;
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
