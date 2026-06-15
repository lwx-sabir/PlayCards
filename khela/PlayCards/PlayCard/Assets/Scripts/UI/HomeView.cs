using System.Threading.Tasks;
using PlayCard.Account;
using PlayCard.App;
using PlayCard.Game.Net;
using PlayCard.Game.Wallet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>Home screen: shows the chips balance HUD and routes into the blackjack lobby.</summary>
    public sealed class HomeView : MonoBehaviour
    {
        [SerializeField] private TMP_Text balanceText;
        [SerializeField] private Button playButton;

        private void Awake()
        {
            if (playButton != null) playButton.onClick.AddListener(SceneNavigator.GoToLobby);
        }

        private void OnEnable()
        {
            if (WalletManager.Instance != null)
                WalletManager.Instance.OnBalancesChanged += ShowBalance;

            if (AccountManager.Instance != null)
            {
                if (AccountManager.Instance.IsReady) _ = LoadAsync();
                else AccountManager.Instance.OnReady += HandleReady;
            }
        }

        private void OnDisable()
        {
            if (WalletManager.Instance != null)
                WalletManager.Instance.OnBalancesChanged -= ShowBalance;
            if (AccountManager.Instance != null)
                AccountManager.Instance.OnReady -= HandleReady;
        }

        private void HandleReady() => _ = LoadAsync();

        private async Task LoadAsync()
        {
            if (WalletManager.Instance != null) await WalletManager.Instance.RefreshAsync();
        }

        private void ShowBalance(WalletBalances b)
        {
            if (balanceText != null && b != null) balanceText.text = $"{b.Chips:0}";
        }
    }
}
