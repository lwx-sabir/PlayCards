using PlayCard.Game.Net;      // WalletBalances
using PlayCard.Game.Wallet;   // WalletManager
using TMPro;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Binds a TMP label to the player's live wallet balance (Chips by default). Drop it on any balance text
    /// in any scene (Home / Lobby / Table) — it pulls from <see cref="WalletManager"/> and updates whenever
    /// the balance changes (e.g. after a bet or settle). Server-authoritative: it only displays the wallet.
    /// </summary>
    public sealed class BalanceHud : MonoBehaviour
    {
        public enum Currency { Chips, Coins, Gems, Tokens }

        [SerializeField] private TMP_Text label;
        [SerializeField] private Currency currency = Currency.Chips;
        [Tooltip("Numeric format, e.g. \"0\" or \"#,0\".")]
        [SerializeField] private string format = "#,0";

        private void Reset() => label = GetComponent<TMP_Text>();

        private void OnEnable()
        {
            if (label == null) label = GetComponent<TMP_Text>();
            if (WalletManager.Instance != null)
            {
                WalletManager.Instance.OnBalancesChanged += Show;
                _ = WalletManager.Instance.RefreshAsync();
            }
        }

        private void OnDisable()
        {
            if (WalletManager.Instance != null) WalletManager.Instance.OnBalancesChanged -= Show;
        }

        private void Show(WalletBalances b)
        {
            if (label == null || b == null) return;
            decimal v = currency switch
            {
                Currency.Coins => b.Coins,
                Currency.Gems => b.Gems,
                Currency.Tokens => b.Tokens,
                _ => b.Chips,
            };
            label.text = v.ToString(format);
        }
    }
}
