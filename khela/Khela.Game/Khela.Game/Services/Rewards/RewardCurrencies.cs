using System;
using Khela.Game.Database.Models;

namespace Khela.Game.Services.Rewards
{
    /// <summary>
    /// Which currencies may move through which CHANNEL. Three ALLOWLISTS, so every one fails CLOSED: the tradeable token,
    /// any undefined currency int, and any FUTURE appended <see cref="CurrencyType"/> value are rejected by construction —
    /// a typo in an admin config or a hand-written Redis override can never re-open the legal guardrail (CLAUDE.md
    /// NON-NEGOTIABLE #2/#4: players never WIN the token by playing).
    ///
    /// One list is not enough (docs/VIP_SPEC.md §1). The three point currencies have deliberately different rules:
    /// <list type="bullet">
    /// <item><b>VIP-P</b> is the record of MONEY — sellable in the store, never granted by play, never exchanged. One shared
    /// list would let a chest mint it or an admin author Chips → VipPoints, and VIP level (and the cashback it pays) would
    /// become farmable.</item>
    /// <item><b>SP</b> is seasonal STATUS — grantable and sellable, but never exchanged: status is not value to be sold on.</item>
    /// <item><b>LP</b> is a comp — grantable, sellable, and exchangeable but only ONE WAY (LP → chips). Chips → LP would be a
    /// round trip into the comp track.</item>
    /// </list>
    /// Enforced twice on every path: once when an admin SAVES a config, and again at GRANT / EXCHANGE time.
    /// </summary>
    public static class RewardCurrencies
    {
        /// <summary>Currencies a REWARD system may hand out (pass, chests, daily, missions, level-ups, the world).</summary>
        private static readonly CurrencyType[] Grantable =
            { CurrencyType.Chips, CurrencyType.Coins, CurrencyType.Gems, CurrencyType.Kash, CurrencyType.Sp, CurrencyType.Lp };

        /// <summary>Currencies a STORE product may pay. Everything grantable, plus VIP-P — which only money may buy.</summary>
        private static readonly CurrencyType[] Sellable =
            { CurrencyType.Chips, CurrencyType.Coins, CurrencyType.Gems, CurrencyType.Kash, CurrencyType.Sp, CurrencyType.Lp, CurrencyType.VipPoints };

        /// <summary>Currencies an exchange pair may take FROM a player.</summary>
        private static readonly CurrencyType[] ExchangeFrom =
            { CurrencyType.Chips, CurrencyType.Coins, CurrencyType.Gems, CurrencyType.Kash, CurrencyType.Lp };

        /// <summary>Currencies an exchange pair may give TO a player. LP is absent on purpose: chips → LP would buy comp.</summary>
        private static readonly CurrencyType[] ExchangeTo =
            { CurrencyType.Chips, CurrencyType.Coins, CurrencyType.Gems, CurrencyType.Kash };

        /// <summary>True only for a currency a reward system may hand out.</summary>
        public static bool IsGrantable(CurrencyType c) => Array.IndexOf(Grantable, c) >= 0;

        /// <summary>True only for a currency a store product may pay.</summary>
        public static bool IsSellable(CurrencyType c) => Array.IndexOf(Sellable, c) >= 0;

        /// <summary>True only for a currency an exchange may take from a player.</summary>
        public static bool IsExchangeableFrom(CurrencyType c) => Array.IndexOf(ExchangeFrom, c) >= 0;

        /// <summary>True only for a currency an exchange may give to a player.</summary>
        public static bool IsExchangeableTo(CurrencyType c) => Array.IndexOf(ExchangeTo, c) >= 0;

        /// <summary>The tradeable token specifically — kept for a precise error message. The real gates are the lists.</summary>
        public static bool IsForbidden(CurrencyType c) => c == CurrencyType.Tokens;

        /// <summary>Human-readable lists for validation messages.</summary>
        public static string GrantableList => string.Join(", ", Grantable);
        public static string SellableList => string.Join(", ", Sellable);
        public static string ExchangeFromList => string.Join(", ", ExchangeFrom);
        public static string ExchangeToList => string.Join(", ", ExchangeTo);

        /// <summary>
        /// Parse a reward line's currency id ("Chips", "kash", …). Names ONLY — a numeric string is rejected, so a
        /// config saying "3" can never resolve to <see cref="CurrencyType.Tokens"/> by accident. Does NOT apply any
        /// allowlist; callers pair this with a channel gate so they can report *why* a value was refused.
        /// </summary>
        public static bool TryParse(string id, out CurrencyType currency)
        {
            currency = default;
            if (string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();
            foreach (var ch in id) if (ch >= '0' && ch <= '9') return false;   // reject numeric forms
            return Enum.TryParse(id, ignoreCase: true, out currency) && Enum.IsDefined(typeof(CurrencyType), currency);
        }

        /// <summary>Parse AND gate for the REWARD channel — the form reward granters use.</summary>
        public static bool TryParseGrantable(string id, out CurrencyType currency)
            => TryParse(id, out currency) && IsGrantable(currency);

        /// <summary>Parse AND gate for the STORE channel — the form the store's grant path uses.</summary>
        public static bool TryParseSellable(string id, out CurrencyType currency)
            => TryParse(id, out currency) && IsSellable(currency);
    }
}
