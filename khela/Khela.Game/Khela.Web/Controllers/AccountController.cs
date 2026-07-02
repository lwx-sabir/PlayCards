using System;
using System.Linq;
using System.Threading.Tasks;
using Khela.Game.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Admin login/logout for the dashboard. Authenticates against the SAME ApplicationUser store as the game
    /// (cookie, not JWT). A valid password is not enough — the account must also pass the admin check
    /// (Development is open; otherwise its id must be in Admin:UserIds) or it's refused WITHOUT a session, so a
    /// non-admin never even gets a cookie.
    /// </summary>
    [AllowAnonymous]
    public class AccountController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signIn;
        private readonly UserManager<ApplicationUser> _users;
        private readonly bool _devOpen;
        private readonly string[] _adminIds;

        public AccountController(SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
            IConfiguration config, IWebHostEnvironment env)
        {
            _signIn = signIn;
            _users = users;
            _devOpen = env.IsDevelopment();
            _adminIds = config.GetSection("Admin:UserIds").Get<string[]>() ?? Array.Empty<string>();
        }

        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true) return RedirectToLocal(returnUrl);
            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string? email, string? password, string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;

            var user = await _users.FindByEmailAsync(email ?? "");
            if (user != null && await _users.CheckPasswordAsync(user, password ?? ""))
            {
                if (_devOpen || _adminIds.Contains(user.Id, StringComparer.OrdinalIgnoreCase))
                {
                    await _signIn.SignInAsync(user, isPersistent: true);   // only NOW is a session created
                    return RedirectToLocal(returnUrl);
                }
                ModelState.AddModelError(string.Empty, "This account is not an administrator.");
                return View();
            }

            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View();
        }

        [HttpGet]
        public IActionResult Register()
        {
            if (User.Identity?.IsAuthenticated == true) return RedirectToLocal(null);
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(string? email, string? password)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                ModelState.AddModelError(string.Empty, "Email and password are required.");
                return View();
            }

            // CountryCode is NOT-NULL in the schema with no default — set it like Khela.Game's own register does.
            var user = new ApplicationUser { UserName = email, Email = email, CountryCode = "bd" };
            var result = await _users.CreateAsync(user, password);
            if (result.Succeeded)
            {
                await _signIn.SignInAsync(user, isPersistent: true);   // straight into the console (dev-open admin)
                return RedirectToLocal(null);
            }
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _signIn.SignOutAsync();
            return RedirectToAction(nameof(Login));
        }

        public IActionResult Denied() => View();

        private IActionResult RedirectToLocal(string? returnUrl)
            => Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction("Index", "Home");
    }
}
