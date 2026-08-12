using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Khela.Game.Database.Models;
using Khela.Game.Services.Wallet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class WalletController : ControllerBase
    {
        private readonly IWalletService wallet;
        private readonly IWebHostEnvironment env;

        // Free starting balances so a new guest can play immediately (the social-casino model).
        private const decimal StarterChips = 10000m;
        private const decimal StarterGems = 100m;

        public WalletController(IWalletService wallet, IWebHostEnvironment env)
        {
            this.wallet = wallet;
            this.env = env;
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
                // This is the balance HUD: it is on every screen and the client re-reads it constantly, so it has to
                // be ONE query. It used to run two idempotent starter-grant WRITE transactions and then five
                // separate balance reads — around fifteen database round trips for a read, every single time, with
                // the grants taking row locks that concurrent reads then queued behind. On a database that isn't on
                // the same machine that is seconds of latency the player feels on every screen.
                //
                // The grants still happen, but only for a wallet that genuinely has nothing yet (a brand-new guest,
                // or a top-up of a currency they have never held). Registration already seeds these, so for every
                // established player this endpoint is now a single SELECT.
                var balances = await wallet.GetBalancesAsync(userId);

                if (!balances.ContainsKey(CurrencyType.Chips) || !balances.ContainsKey(CurrencyType.Gems))
                {
                    if (!balances.ContainsKey(CurrencyType.Chips))
                        await wallet.CreditAsync(userId, CurrencyType.Chips, StarterChips, TransactionType.Bonus,
                            $"starter:{userId}:Chips", new WalletContext { Description = "Starter chips" });
                    if (!balances.ContainsKey(CurrencyType.Gems))
                        await wallet.CreditAsync(userId, CurrencyType.Gems, StarterGems, TransactionType.Bonus,
                            $"starter:{userId}:Gems", new WalletContext { Description = "Starter gems" });
                    balances = await wallet.GetBalancesAsync(userId);
                }

                decimal Of(CurrencyType c) => balances.TryGetValue(c, out var v) ? v : 0m;

                return Ok(new
                {
                    Chips  = Of(CurrencyType.Chips),
                    Coins  = Of(CurrencyType.Coins),
                    Gems   = Of(CurrencyType.Gems),
                    Tokens = Of(CurrencyType.Tokens),
                    Kash   = Of(CurrencyType.Kash)
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

        /// <summary>DEV ONLY — credit test Chips to the signed-in player (top-up while testing). Returns 404
        /// outside Development so it can never ship as a money cheat.</summary>
        [HttpPost("dev/chips")]
        public async Task<IActionResult> DevAddChips([FromQuery] decimal amount)
        {
            if (!env.IsDevelopment()) return NotFound();
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");
            if (amount <= 0) return BadRequest(new { message = "amount must be > 0" });

            await wallet.CreditAsync(userId, CurrencyType.Chips, amount, TransactionType.AdminAdjustment,
                $"dev:{Guid.NewGuid():N}", new WalletContext { Description = "Dev test chips" });

            return Ok(new { Chips = await wallet.GetBalanceAsync(userId, CurrencyType.Chips) });
        }

        private string GetUserId() =>
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
    }
}
