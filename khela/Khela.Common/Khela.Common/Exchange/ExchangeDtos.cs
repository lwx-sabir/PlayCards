using System;
using System.Collections.Generic;

namespace Khela.Common.Exchange
{
    /// <summary>
    /// One currency-exchange PAIR as the player sees it: A → B at a fixed rate, with the server's per-player limits.
    /// Pairs are admin-authored (Redis doc <c>khela:exchange</c>); nothing here is decided by the client. The rate is
    /// expressed as <see cref="FromPerUnit"/> — how much of <see cref="FromCurrency"/> buys ONE unit of
    /// <see cref="ToCurrency"/> — because that is the only way a rate like 1,000,000 chips = 1 Kash has no rounding.
    /// </summary>
    public sealed class ExchangePairDto
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        /// <summary>Currency NAMES ("Chips", "Kash", …), the wallet's.</summary>
        public string FromCurrency { get; set; }
        public string ToCurrency { get; set; }
        /// <summary>Units of <see cref="FromCurrency"/> per ONE unit of <see cref="ToCurrency"/>.</summary>
        public decimal FromPerUnit { get; set; }
        /// <summary>Granularity of the TO amount (1 = whole units).</summary>
        public decimal Step { get; set; }
        public decimal MinTo { get; set; }
        /// <summary>0 = no per-exchange ceiling.</summary>
        public decimal MaxToPerTx { get; set; }
        /// <summary>0 = uncapped. Per UTC day, per player, in TO units.</summary>
        public decimal DailyCapTo { get; set; }
        /// <summary>0 = uncapped. Lifetime, per player, in TO units.</summary>
        public decimal LifetimeCapTo { get; set; }
        public int MinLevel { get; set; }
        public int SortOrder { get; set; }
        public DateTime? AvailableToUtc { get; set; }
        /// <summary>False when THIS player can't use it right now; <see cref="Reason"/> says why.</summary>
        public bool Available { get; set; }
        public string Reason { get; set; }
        /// <summary>How much of the TO currency this player has already taken through this pair (today / ever) — for the caps.</summary>
        public decimal UsedToday { get; set; }
        public decimal UsedLifetime { get; set; }
    }

    public sealed class ExchangeCatalogDto
    {
        /// <summary>The exchange as a whole is on (kill switch + catalog switch).</summary>
        public bool Enabled { get; set; }
        public List<ExchangePairDto> Pairs { get; set; }
        /// <summary>The player's balances by currency name — so a quote can be shown without another round trip.</summary>
        public Dictionary<string, decimal> Balances { get; set; }
        public DateTime ServerTimeUtc { get; set; }
    }

    /// <summary>"What would it cost?" — the server's arithmetic, never the client's.</summary>
    public sealed class ExchangeQuoteRequest
    {
        public string PairKey { get; set; }
        public decimal ToAmount { get; set; }
    }

    public sealed class ExchangeQuoteDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string PairKey { get; set; }
        public string FromCurrency { get; set; }
        public string ToCurrency { get; set; }
        public decimal FromAmount { get; set; }
        public decimal ToAmount { get; set; }
        public decimal FromBalance { get; set; }
        public decimal ToBalance { get; set; }
    }

    /// <summary>Do it. <see cref="RequestId"/> makes the call idempotent: a retry with the same id replays the original outcome.</summary>
    public sealed class ExchangeRequest
    {
        public string PairKey { get; set; }
        public decimal ToAmount { get; set; }
        /// <summary>A fresh Guid per tap (the client generates it). The server keys the wallet movements on it.</summary>
        public Guid RequestId { get; set; }
    }

    public sealed class ExchangeResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        /// <summary>True when this RequestId had already been executed — nothing moved again.</summary>
        public bool Replayed { get; set; }
        public Guid? ExchangeId { get; set; }
        public string PairKey { get; set; }
        public string FromCurrency { get; set; }
        public decimal FromAmount { get; set; }
        public string ToCurrency { get; set; }
        public decimal ToAmount { get; set; }
        public decimal NewFromBalance { get; set; }
        public decimal NewToBalance { get; set; }
        /// <summary>Every balance after the exchange, by currency name — the HUD repaints from these.</summary>
        public Dictionary<string, decimal> Balances { get; set; }
        /// <summary>Convenience copies for the chip / Kash HUDs (the client's balance-changing rule keys on these).</summary>
        public decimal NewChipBalance { get; set; }
        public decimal NewKashBalance { get; set; }
    }

    /// <summary>One past exchange, as the player may see it (history).</summary>
    public sealed class ExchangeRecordDto
    {
        public Guid Id { get; set; }
        public string PairKey { get; set; }
        public string FromCurrency { get; set; }
        public decimal FromAmount { get; set; }
        public string ToCurrency { get; set; }
        public decimal ToAmount { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
