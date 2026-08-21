using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Khela.Game.Database.Models;
using Khela.Game.Services.Wallet;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Rewards
{
    /// <summary>
    /// Grants a wallet currency. The credit runs through the idempotent <see cref="IWalletService"/> (audited,
    /// row-locked, signed-delta ledger) and reports the amount the WALLET applied.
    ///
    /// The allowlist (<see cref="RewardCurrencies"/>) is re-checked HERE, not just at config-save time: this is the
    /// last gate before the ledger, so a hand-written Redis override or a future appended currency can never be
    /// credited by a reward system (CLAUDE.md NON-NEGOTIABLE #2/#4).
    /// </summary>
    public sealed class CurrencyGranter : IRewardGranter
    {
        private readonly IWalletService _wallet;
        private readonly ILogger<CurrencyGranter> _logger;

        public CurrencyGranter(IWalletService wallet, ILogger<CurrencyGranter> logger)
        {
            _wallet = wallet; _logger = logger;
        }

        public RewardKind Kind => RewardKind.Currency;

        public async Task<IReadOnlyList<GrantedLineDto>> GrantAsync(Guid userId, RewardGrant line, string idemKey, string description, string externalRef = null)
        {
            if (line == null || line.Amount <= 0m) return null;

            if (!RewardCurrencies.TryParse(line.Id, out var currency))
            {
                _logger.LogWarning("Reward line skipped: '{Id}' is not a currency name.", line.Id);
                return null;
            }
            if (!RewardCurrencies.IsAllowed(currency))
            {
                // Fail-closed. Loud, because reaching here means a config bypassed admin-save validation.
                _logger.LogError("Reward line REFUSED: {Currency} may never be granted{Token}.",
                    currency, RewardCurrencies.IsForbidden(currency) ? " (tradeable token)" : "");
                return null;
            }

            var txn = await _wallet.CreditAsync(userId.ToString(), currency, line.Amount, TransactionType.Bonus,
                RewardIds.WalletKey(idemKey), new WalletContext { Description = description ?? "Reward", ExternalRef = externalRef ?? idemKey });

            return new[]
            {
                new GrantedLineDto
                {
                    Kind = (int)RewardKind.Currency,
                    Id = currency.ToString(),
                    Amount = txn?.Amount ?? line.Amount,   // the APPLIED delta, not the requested one
                    Balance = txn?.BalanceAfter ?? 0m,     // computed under the row lock — saves the caller a re-read
                    Images = line.Images,                  // so the collect animation shows the ladder's own art
                },
            };
        }
    }
}
