using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Database.Models;
using Khela.Game.Services.Wallet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class WalletController : ControllerBase
    {
        private readonly IWalletService wallet;

        // Free starting balances so a new guest can play immediately (the social-casino model).
        private const decimal StarterChips = 10000m;
        private const decimal StarterGems = 100m;

        public WalletController(IWalletService wallet)
        {
            this.wallet = wallet;
        }

        /// <summary>
        /// All currency balances for the signed-in user — the balance HUD shown on every screen.
        /// Lazily applies the one-time starter grant on first call (idempotent on correlation id,
        /// so it never grants twice no matter how often the HUD refreshes).
        /// </summary>
        [HttpGet("balances")]
        public async Task<IActionResult> GetBalances()
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                await wallet.CreditAsync(userId, CurrencyType.Chips, StarterChips, TransactionType.Bonus,
                    $"starter:{userId}:Chips", new WalletContext { Description = "Starter chips" });
                await wallet.CreditAsync(userId, CurrencyType.Gems, StarterGems, TransactionType.Bonus,
                    $"starter:{userId}:Gems", new WalletContext { Description = "Starter gems" });

                return Ok(new
                {
                    Chips = await wallet.GetBalanceAsync(userId, CurrencyType.Chips),
                    Coins = await wallet.GetBalanceAsync(userId, CurrencyType.Coins),
                    Gems = await wallet.GetBalanceAsync(userId, CurrencyType.Gems),
                    Tokens = await wallet.GetBalanceAsync(userId, CurrencyType.Tokens),
                    Kash = await wallet.GetBalanceAsync(userId, CurrencyType.Kash)
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Balance for a single currency.</summary>
        [HttpGet("balance/{currency}")]
        public async Task<IActionResult> GetBalance(CurrencyType currency)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            var balance = await wallet.GetBalanceAsync(userId, currency);
            return Ok(new { Currency = currency.ToString(), Balance = balance });
        }

        private string GetUserId() =>
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
    }
}
