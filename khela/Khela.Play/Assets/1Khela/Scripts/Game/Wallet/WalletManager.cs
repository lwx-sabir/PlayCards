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
        ///
        /// NOTE: there is deliberately NO optimistic/predicted component here. An earlier version showed a stake leaving
        /// before the server confirmed it, but every action also triggers a balance refresh, so a refresh already in
        /// flight would come back carrying the debit and the prediction got applied on top of it — the balance dipped
        /// twice and then corrected upward, flashing a "win" green for a bet. The latency that prediction was hiding is
        /// gone anyway: TableController now paints the HUD straight from the board snapshot's own wallet mirror, which
        /// arrives with the push instead of needing a second round-trip.
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

        // Bumped on every authoritative write. A GET /wallet issued BEFORE a write can land AFTER it (the request is
        // in flight across the write), and applying that stale response rolls the balance back to its pre-write value.
        // On a natural blackjack the deal-debit refresh and the settle credit are only one driver tick apart, which is
        // exactly the window where the win visibly "un-credits" itself.
        private int _writeGen;

        /// <summary>Set the canonical balances and broadcast. The ONE place balances are written — every HUD reads from here.</summary>
        public void Apply(WalletBalances balances)
        {
            if (balances == null) return;
            Balances = balances;
            _writeGen++;
            OnBalancesChanged?.Invoke(Balances);
        }

        /// <summary>Instant partial update of just Chips (e.g. from a claim response or a table board snapshot) — no
        /// server round-trip. Other currencies follow on the next <see cref="RefreshAsync"/>.</summary>
        public void SetChips(decimal chips)
        {
            Balances ??= new WalletBalances();
            Balances.Chips = chips;
            _writeGen++;
            OnBalancesChanged?.Invoke(Balances);
        }

        // SINGLE-FLIGHT. Every HUD refreshes on enable and TableController.Do kicks one after EVERY action, so joining
        // a table fired five or six identical /wallet/balances at once and each action added another. On a phone that
        // is the bulk of the REST traffic, and Best HTTP only opens a few connections per host — the surplus queues
        // behind the ones that matter (bet, deal, stand) and they time out. Callers that arrive while a fetch is in
        // flight now await THAT fetch instead of starting their own; the result is identical, since they all want the
        // same server value.
        private Task<bool> _inFlight;

        /// <summary>Re-fetch balances from the server. Call after settles, purchases, or on screen load.</summary>
        public Task<bool> RefreshAsync()
        {
            if (_inFlight != null && !_inFlight.IsCompleted) return _inFlight;
            _inFlight = RefreshCoreAsync();
            return _inFlight;
        }

        private async Task<bool> RefreshCoreAsync()
        {
            int gen = _writeGen;
            var res = await BlackjackRestClient.Instance.GetWalletAsync();
            if (!res.Ok)
            {
                Debug.LogWarning($"[WalletManager] balance fetch failed: {res.Error}");
                return false;
            }

            // A newer authoritative value landed while this was in flight, so this response is already out of date —
            // applying it would roll the balance BACKWARDS (a settle credit being undone by a fetch that was issued
            // before it). Drop it; whatever wrote in the meantime is closer to the truth, and the next refresh will
            // reconcile anyway.
            if (_writeGen != gen) return true;

            Apply(res.Value);
            return true;
        }
    }
}
