using System;
using System.Threading.Tasks;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.Game.Wallet
{
    /// <summary>
    /// Fetches and caches the signed-in player's wallet balances (the chips/gems HUD on every screen)
    /// and raises <see cref="OnBalancesChanged"/> so HUD widgets refresh. The server is authoritative;
    /// this is a display cache. The first fetch also triggers the server's idempotent starter grant.
    /// </summary>
    public sealed class WalletManager : MonoBehaviour
    {
        public static WalletManager Instance { get; private set; }

        public WalletBalances Balances { get; private set; }
        /// <summary>Current Chips balance (0 if not yet fetched). Chips are the wagerable currency.</summary>
        public decimal Chips => Balances?.Chips ?? 0m;
        /// <summary>Current Kash balance (0 if not yet fetched). Kash is the premium spend currency (non-wagerable).</summary>
        public decimal Kash => Balances?.Kash ?? 0m;
        public event Action<WalletBalances> OnBalancesChanged;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            // Any balance-changing REST call (claim, redeem, …) signals here → re-pull → every HUD updates. No caller
            // needs to remember to refresh.
            BlackjackRestClient.BalanceMaybeChanged += HandleBalanceMaybeChanged;
        }

        private void OnDestroy()
        {
            if (Instance == this) BlackjackRestClient.BalanceMaybeChanged -= HandleBalanceMaybeChanged;
        }

        // A balance-changing call succeeded. Apply the chip hint INSTANTLY (no round-trip), then reconcile every
        // currency from the server. Either step fires OnBalancesChanged, so all HUDs repaint.
        private void HandleBalanceMaybeChanged(decimal? knownChips)
        {
            if (knownChips.HasValue) SetChips(knownChips.Value);
            _ = RefreshAsync();
        }

        /// <summary>Set the canonical balances and broadcast. The ONE place balances are written — every HUD reads from here.</summary>
        public void Apply(WalletBalances balances)
        {
            if (balances == null) return;
            Balances = balances;
            OnBalancesChanged?.Invoke(Balances);
        }

        /// <summary>Instant partial update of just Chips (e.g. from a claim response or a table board snapshot) — no
        /// server round-trip. Other currencies follow on the next <see cref="RefreshAsync"/>.</summary>
        public void SetChips(decimal chips)
        {
            Balances ??= new WalletBalances();
            Balances.Chips = chips;
            OnBalancesChanged?.Invoke(Balances);
        }

        /// <summary>Re-fetch balances from the server. Call after settles, purchases, or on screen load.</summary>
        public async Task<bool> RefreshAsync()
        {
            var res = await BlackjackRestClient.Instance.GetWalletAsync();
            if (!res.Ok)
            {
                Debug.LogWarning($"[WalletManager] balance fetch failed: {res.Error}");
                return false;
            }

            Apply(res.Value);
            return true;
        }
    }
}
