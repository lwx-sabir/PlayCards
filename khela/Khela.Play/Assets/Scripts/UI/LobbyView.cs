using System.Collections.Generic;
using System.Threading.Tasks;
using PlayCard.App;
using PlayCard.Game.Net;
using PlayCard.Game.Wallet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The blackjack lobby (table browser): lists tables from GET /api/lobby/blackjack, lets the player
    /// join one (taking a seat) and opens the Table scene. Shows the chips balance via WalletManager.
    /// </summary>
    public sealed class LobbyView : MonoBehaviour
    {
        [Header("List")]
        [SerializeField] private Transform listContainer;
        [SerializeField] private TableRowView rowPrefab;

        [Header("Chrome")]
        [SerializeField] private Button refreshButton;
        [SerializeField] private Button backButton;
        [SerializeField] private TMP_Text balanceText;
        [SerializeField] private TMP_Text statusText;

        private readonly List<TableRowView> _rows = new List<TableRowView>();
        private bool _joining;

        private void Awake()
        {
            if (refreshButton != null) refreshButton.onClick.AddListener(() => _ = RefreshAsync());
            if (backButton != null) backButton.onClick.AddListener(SceneNavigator.GoToHome);
        }

        private async void OnEnable()
        {
            if (WalletManager.Instance != null)
            {
                WalletManager.Instance.OnBalancesChanged += ShowBalance;
                await WalletManager.Instance.RefreshAsync();
            }
            await RefreshAsync();
        }

        private void OnDisable()
        {
            if (WalletManager.Instance != null) WalletManager.Instance.OnBalancesChanged -= ShowBalance;
        }

        private async Task RefreshAsync()
        {
            SetStatus("Loading tables…");
            var res = await BlackjackRestClient.Instance.GetLobbyAsync();
            if (!res.Ok)
            {
                SetStatus($"Couldn't load tables: {res.Error}");
                return;
            }

            ClearRows();
            var tables = res.Value ?? new List<Khela.Common.Blackjack.BlackjackTableSummary>();
            foreach (var summary in tables)
            {
                if (rowPrefab == null || listContainer == null) break;
                var row = Instantiate(rowPrefab, listContainer);
                row.Bind(summary, JoinTable);
                _rows.Add(row);
            }

            SetStatus(tables.Count == 0 ? "No tables yet." : string.Empty);
        }

        private async void JoinTable(string tableId)
        {
            if (_joining) return;
            _joining = true;
            SetStatus("Joining…");

            var res = await BlackjackRestClient.Instance.JoinAsync(tableId, "Player");
            if (res.Ok)
            {
                SceneNavigator.GoToTable(tableId);
            }
            else
            {
                SetStatus($"Join failed: {res.Error}");
                _joining = false;
            }
        }

        private void ShowBalance(WalletBalances b)
        {
            if (balanceText != null && b != null) balanceText.text = $"{b.Chips:0}";
        }

        private void ClearRows()
        {
            foreach (var row in _rows)
                if (row != null) Destroy(row.gameObject);
            _rows.Clear();
        }

        private void SetStatus(string s) { if (statusText != null) statusText.text = s; }
    }
}
