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
        public event Action<WalletBalances> OnBalancesChanged;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
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

            Balances = res.Value;
            OnBalancesChanged?.Invoke(Balances);
            return true;
        }
    }
}
