using System;
using Khela.Game.Database.Models;

namespace Khela.Game.Services.Rewards
{
    /// <summary>
    /// The single source of truth for "which currencies may be GIVEN AWAY by a reward system" (pass, chests, missions,
    /// level-ups). An ALLOWLIST, so it fails CLOSED: the tradeable token, any undefined currency int, and any FUTURE
    /// appended <see cref="CurrencyType"/> value are all rejected by construction — a typo in an admin config or a
    /// hand-written Redis override can never re-open the legal guardrail (CLAUDE.md NON-NEGOTIABLE #2/#4: players
    /// never WIN the token by playing).
    ///
    /// Enforced twice on every path: once when an admin SAVES a config, and again at GRANT time.
    /// </summary>
    public static class RewardCurrencies
    {
        private static readonly CurrencyType[] Allowed =
            { CurrencyType.Chips, CurrencyType.Coins, CurrencyType.Gems, CurrencyType.Kash };

        /// <summary>True only for a currency a reward system may hand out.</summary>
        public static bool IsAllowed(CurrencyType c) => Array.IndexOf(Allowed, c) >= 0;

        /// <summary>The tradeable token specifically — kept for a precise error message. The real gate is
        /// <see cref="IsAllowed"/>.</summary>
        public static bool IsForbidden(CurrencyType c) => c == CurrencyType.Tokens;

        /// <summary>Human-readable allowlist for validation messages.</summary>
        public static string AllowedList => string.Join(", ", Allowed);

        /// <summary>
        /// Parse a reward line's currency id ("Chips", "kash", …). Names ONLY — a numeric string is rejected, so a
        /// config saying "3" can never resolve to <see cref="CurrencyType.Tokens"/> by accident. Does NOT apply the
        /// allowlist; callers pair this with <see cref="IsAllowed"/> so they can report *why* a value was refused.
        /// </summary>
        public static bool TryParse(string id, out CurrencyType currency)
        {
            currency = default;
            if (string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();
            foreach (var ch in id) if (ch >= '0' && ch <= '9') return false;   // reject numeric forms
            return Enum.TryParse(id, ignoreCase: true, out currency) && Enum.IsDefined(typeof(CurrencyType), currency);
        }

        /// <summary>Parse AND allowlist-gate in one call — the form granters use.</summary>
        public static bool TryParseAllowed(string id, out CurrencyType currency)
            => TryParse(id, out currency) && IsAllowed(currency);
    }
}
