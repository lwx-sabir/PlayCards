using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Exchange;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.Exchange
{
    /// <summary>
    /// The server's exchange catalog as this player sees it (GET /api/exchange): the pairs the admin authored, each with
    /// THIS player's availability and usage against its caps, plus the player's balances. Plain singleton in the
    /// <c>StoreCatalog</c> shape — cached, refreshed on demand, <see cref="Changed"/> for binders — and the one place the
    /// screen asks for a quote or fires an exchange. Nothing here decides anything: rates, limits, costs and refusals are all
    /// server answers; this only carries them and generates the per-tap request id that makes a retry safe.
    /// </summary>
    public sealed class ExchangeState
    {
        private static ExchangeState _instance;
        public static ExchangeState Instance => _instance ??= new ExchangeState();

        /// <summary>Fires after every successful refresh and after every completed exchange (the catalog is re-read then).</summary>
        public event Action<ExchangeCatalogDto> Changed;
        /// <summary>Fires with the result of every exchange attempt (success or refusal) — for the screen's feedback.</summary>
        public event Action<ExchangeResultData> Exchanged;

        public ExchangeCatalogDto Current { get; private set; }
        public bool Loaded => Current != null;
        public bool Enabled => Current != null && Current.Enabled;
        public IReadOnlyList<ExchangePairDto> Pairs => Current?.Pairs ?? (IReadOnlyList<ExchangePairDto>)Array.Empty<ExchangePairDto>();

        private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);
        private bool _refreshing;
        private bool _exchanging;
        private DateTime _fetchedAtUtc;

        private static bool IsSignedIn
            => PlayCard.Account.AccountManager.Instance == null
            || !string.IsNullOrEmpty(PlayCard.Account.AccountManager.Instance.JwtToken);

        /// <summary>Is an exchange in flight? The screen disables its button on this — one tap, one exchange.</summary>
        public bool Busy => _exchanging;

        private bool _refreshAgain;

        public async Task<ExchangeCatalogDto> RefreshAsync(bool force = false)
        {
            // A forced refresh that arrives while one is in flight (the screen was refreshing when an exchange completed) is
            // NOT dropped: the in-flight answer was issued before the exchange and would stamp stale usage/balances as fresh
            // for the whole freshness window. Remember it and go once more when the current one lands.
            if (_refreshing) { if (force) _refreshAgain = true; return Current; }
            if (!IsSignedIn) return Current;
            if (!force && Current != null && DateTime.UtcNow - _fetchedAtUtc < Freshness) return Current;
            _refreshing = true;
            try
            {
                do
                {
                    _refreshAgain = false;
                    var result = await BlackjackRestClient.Instance.GetExchangeCatalogAsync();
                    if (!result.Ok || result.Value == null)
                    {
                        Debug.LogWarning($"[ExchangeState] fetch failed: {result.Error}");
                        continue;
                    }
                    Current = result.Value;
                    _fetchedAtUtc = DateTime.UtcNow;
                    Changed?.Invoke(Current);
                }
                while (_refreshAgain);
                return Current;
            }
            finally { _refreshing = false; _refreshAgain = false; }
        }

        public bool TryGet(string pairKey, out ExchangePairDto pair)
        {
            pair = Pairs.FirstOrDefault(p => p != null && string.Equals(p.Key, pairKey, StringComparison.OrdinalIgnoreCase));
            return pair != null;
        }

        /// <summary>The player's balance in a currency as of the last catalog read ("Chips", "Kash", …).</summary>
        public decimal BalanceOf(string currency)
            => Current?.Balances != null && Current.Balances.TryGetValue(currency ?? "", out var v) ? v : 0m;

        /// <summary>Ask the server what <paramref name="toAmount"/> would cost — no writes. Null on a transport failure.</summary>
        public async Task<ExchangeQuoteDto> QuoteAsync(string pairKey, decimal toAmount)
        {
            var result = await BlackjackRestClient.Instance.ExchangeQuoteAsync(pairKey, toAmount);
            if (!result.Ok) { Debug.LogWarning($"[ExchangeState] quote failed: {result.Error}"); return null; }
            return result.Value;
        }

        /// <summary>
        /// Do it. A fresh request id per call; the server keys the wallet movements on it, so a retry of THIS call (same id)
        /// can never move money twice. One in flight at a time. The balances HUD repaints itself (BalanceChangingAsync); the
        /// catalog is re-read afterwards so caps and balances on the screen are current.
        /// </summary>
        public async Task<ExchangeResultData> ExchangeAsync(string pairKey, decimal toAmount)
        {
            if (_exchanging) return new ExchangeResultData { Ok = false, Error = "An exchange is already in progress." };
            _exchanging = true;
            var requestId = Guid.NewGuid();
            try
            {
                var result = await BlackjackRestClient.Instance.ExchangeAsync(pairKey, toAmount, requestId);
                var data = result.Ok && result.Value != null ? result.Value : new ExchangeResultData { Ok = false, Error = result.Error ?? "Could not reach the server.", PairKey = pairKey };
                Exchanged?.Invoke(data);
                if (data.Ok) _ = RefreshAsync(force: true);
                return data;
            }
            finally { _exchanging = false; }
        }
    }
}
