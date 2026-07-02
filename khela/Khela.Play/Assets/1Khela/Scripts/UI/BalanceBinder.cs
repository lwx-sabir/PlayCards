using PlayCard.Game.Net;
using PlayCard.Game.Wallet;
using TMPro;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Drop-in balance HUD for ANY screen. Assign a TMP field for each currency you want to show (all optional) and it
    /// keeps them in sync with the player's wallet: it paints the cached balances on enable, then re-pulls, and listens
    /// to <see cref="WalletManager.OnBalancesChanged"/> so it updates after settles / claims / purchases. The server is
    /// authoritative — this only displays. Adding a new currency = one field + one line in <see cref="Show"/>.
    /// </summary>
    public sealed class BalanceBinder : MonoBehaviour
    {
        [Header("Assign the text for any currency you show (all optional)")]
        [SerializeField] private TMP_Text chipsText;
        [SerializeField] private TMP_Text coinsText;
        [SerializeField] private TMP_Text gemsText;
        [SerializeField] private TMP_Text kashText;
        [SerializeField] private TMP_Text tokensText;

        [Tooltip("Number format for every balance (e.g. \"#,0\" → 1,234,567; \"0\" → 1234567).")]
        [SerializeField] private string moneyFormat = "#,0";

        private void OnEnable()
        {
            var wm = WalletManager.Instance;
            if (wm == null) return;
            wm.OnBalancesChanged += Show;
            if (wm.Balances != null) Show(wm.Balances);   // paint what we already have, no flicker
            _ = wm.RefreshAsync();                         // then re-pull from the server
        }

        private void OnDisable()
        {
            if (WalletManager.Instance != null) WalletManager.Instance.OnBalancesChanged -= Show;
        }

        private void Show(WalletBalances b)
        {
            if (b == null) return;
            Set(chipsText, b.Chips);
            Set(coinsText, b.Coins);
            Set(gemsText, b.Gems);
            Set(kashText, b.Kash);
            Set(tokensText, b.Tokens);
        }

        private void Set(TMP_Text t, decimal amount) { if (t != null) t.text = amount.ToString(moneyFormat); }
    }
}
