using System;
using System.Collections.Generic;

namespace PlayCard.Game.Net
{
    /// <summary>
    /// Client mirror of the server's <c>ExchangeResultDto</c> (POST /api/exchange) — its own copy, like
    /// <see cref="StoreRedeemResultData"/>, because it must implement <see cref="IChipBalanceResult"/> so the wallet's single
    /// source (<c>WalletManager</c>) repaints every balance HUD from the server's post-exchange balances the instant they land.
    /// The shape is identical to the shared DTO; System.Text.Json maps it case-insensitively.
    /// </summary>
    public sealed class ExchangeResultData : IChipBalanceResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        /// <summary>True when this request id had already been executed — nothing moved again.</summary>
        public bool Replayed { get; set; }
        public Guid? ExchangeId { get; set; }
        public string PairKey { get; set; }
        public string FromCurrency { get; set; }
        public decimal FromAmount { get; set; }
        public string ToCurrency { get; set; }
        public decimal ToAmount { get; set; }
        public decimal NewFromBalance { get; set; }
        public decimal NewToBalance { get; set; }
        /// <summary>Every balance after the exchange, by currency name.</summary>
        public Dictionary<string, decimal> Balances { get; set; } = new Dictionary<string, decimal>();
        public decimal NewChipBalance { get; set; }
        public decimal NewKashBalance { get; set; }
    }
}
