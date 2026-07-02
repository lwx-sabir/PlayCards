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
        [Tooltip("Optional — the Kash balance text (premium currency).")]
        [SerializeField] private TMP_Text kashText;
        [SerializeField] private Button playButton;
        [Tooltip("Opens the user-profile panel on click.")]
        [SerializeField] private Button profileButton;
        [Tooltip("The UserProfile panel GameObject to show — its ProfilePanelBinder fetches + paints on enable.")]
        [SerializeField] private GameObject profilePanel;
        [Tooltip("Opens the daily rewards / missions panel on click.")]
        [SerializeField] private Button rewardsButton;
        [Tooltip("The Rewards_Daily / Missions panel to show — its binders fetch + paint on enable.")]
        [SerializeField] private GameObject rewardsPanel;

        private void Awake()
        {
            if (playButton != null) playButton.onClick.AddListener(SceneNavigator.GoToLobby);
            if (profileButton != null) profileButton.onClick.AddListener(OpenProfile);
            if (rewardsButton != null) rewardsButton.onClick.AddListener(OpenRewards);
        }

        /// <summary>Show the user-profile panel; its ProfilePanelBinder fetches + repaints when it enables.</summary>
        private void OpenProfile()
        {
            if (profilePanel != null) profilePanel.SetActive(true);
        }

        /// <summary>Show the daily rewards / missions panel; its binders fetch + repaint when it enables.</summary>
        private void OpenRewards()
        {
            if (rewardsPanel != null) rewardsPanel.SetActive(true);
        }

        private void OnEnable()
        {
            if (WalletManager.Instance != null)
            {
                WalletManager.Instance.OnBalancesChanged += ShowBalance;
                if (WalletManager.Instance.Balances != null) ShowBalance(WalletManager.Instance.Balances);   // paint cached first — no stale frame on (re)enter
            }

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
            if (b == null) return;
            if (balanceText != null) balanceText.text = $"{b.Chips:0}";
            if (kashText != null) kashText.text = $"{b.Kash:0}";
        }
    }
}
