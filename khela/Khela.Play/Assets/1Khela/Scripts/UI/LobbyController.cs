using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Blackjack;
using PlayCard.App;
using PlayCard.Game.Net;
using PlayCard.Game.Wallet;
using PlayCard.Home;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Lobby = a live carousel of open tables for the game chosen on Home (<see cref="GameSession.SelectedGame"/>).
    /// Fetches GET /api/lobby/blackjack?mode= and spawns a <see cref="LobbyTableCard"/> per table under the
    /// carousel root, then tells the <see cref="CarouselController"/> to rebuild. The shared HUD <b>Join</b>
    /// seats the player at the centred table; the <c>&lt; &gt;</c> arrows are wired to the carousel's Prev/Next.
    /// (v1: blackjack only; mode tabs + stakes-tier filter come later.)
    /// </summary>
    public sealed class LobbyController : MonoBehaviour
    {
        [Header("Carousel")]
        [Tooltip("CarouselController on the ring root (also the parent the cards spawn under).")]
        [SerializeField] private CarouselController carousel;
        [SerializeField] private Transform cardParent;       // usually the carousel root's transform
        [SerializeField] private LobbyTableCard cardPrefab;

        [Header("Centred-table HUD")]
        [SerializeField] private Button joinButton;          // joins the centred table
        [SerializeField] private TMP_Text betRangeText;      // centre stake text, mirrors the centred card
        [SerializeField] private TMP_Text membersText;       // "2/5" for the centred table

        [Header("Chrome")]
        [SerializeField] private Button backButton;
        [SerializeField] private Button refreshButton;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text statusText;
        [Tooltip("HUD status/loading-spinner container — shown only while there's a message, hidden otherwise. " +
                 "Falls back to the statusText's own GameObject if left empty.")]
        [SerializeField] private GameObject statusRoot;
        [SerializeField] private TMP_Text balanceText;

        [Header("Filter")]
        [SerializeField] private BlackjackMode mode = BlackjackMode.Classic;

        private readonly List<LobbyTableCard> _cards = new();
        private LobbyTableCard _current;
        private bool _loading;

        private void Awake()
        {
            if (backButton) backButton.onClick.AddListener(SceneNavigator.GoToHome);
            if (refreshButton) refreshButton.onClick.AddListener(() => _ = RefreshAsync());
            if (joinButton) joinButton.onClick.AddListener(JoinCurrent);
        }

        private async void OnEnable()
        {
            if (carousel) carousel.OnSelectionChanged += OnSelected;
            if (titleText) titleText.text = (GameSession.SelectedGame ?? "blackjack").ToUpperInvariant();

            if (WalletManager.Instance != null)
            {
                WalletManager.Instance.OnBalancesChanged += ShowBalance;
                await WalletManager.Instance.RefreshAsync();
            }
            await RefreshAsync();
        }

        private void OnDisable()
        {
            if (carousel) carousel.OnSelectionChanged -= OnSelected;
            if (WalletManager.Instance != null) WalletManager.Instance.OnBalancesChanged -= ShowBalance;
        }

        /// <summary>Switch rule variant — hook the mode tabs (Classic/Hi-Lo/…) here. Re-fetches.</summary>
        public void SetMode(int modeIndex)
        {
            mode = (BlackjackMode)modeIndex;
            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (_loading) return;
            _loading = true;
            SetStatus("Loading tables…");

            var res = await BlackjackRestClient.Instance.GetLobbyAsync(mode);
            if (!res.Ok)
            {
                SetStatus($"Couldn't load tables: {res.Error}");
                _loading = false;
                return;
            }

            ClearCards();
            var tables = res.Value ?? new List<BlackjackTableSummary>();
            foreach (var summary in tables)
            {
                if (!cardPrefab || !cardParent) break;
                var card = Instantiate(cardPrefab, cardParent);
                card.Bind(summary);
                card.OnStatus += SetStatus;   // card's join/loading feedback → HUD status
                _cards.Add(card);
            }

            if (carousel) carousel.Rebuild();   // re-scan + centre → fires OnSelected for the centred card
            SetStatus(tables.Count == 0 ? "No tables yet." : string.Empty);
            _loading = false;
        }

        // Centred table changed (drag / arrows / rebuild): mirror its stakes + seats to the HUD.
        private void OnSelected(ICarouselItem item)
        {
            _current = item as LobbyTableCard;
            if (betRangeText) betRangeText.text = _current != null ? _current.BetLabel : string.Empty;
            if (membersText) membersText.text = _current != null ? _current.PlayersLabel : string.Empty;
            if (joinButton) joinButton.interactable = _current != null && _current.CanJoin;
        }

        private void JoinCurrent() => _current?.Join();

        private void ClearCards()
        {
            foreach (var c in _cards)
                if (c) { c.OnStatus -= SetStatus; Destroy(c.gameObject); }
            _cards.Clear();
            _current = null;
        }

        private void ShowBalance(WalletBalances b)
        {
            if (balanceText && b != null) balanceText.text = $"{b.Chips:0}";
        }

        // Show the HUD status/spinner only while there's a message; hide it when cleared.
        private void SetStatus(string s)
        {
            bool has = !string.IsNullOrEmpty(s);
            if (statusText && has) statusText.text = s;
            if (statusRoot) statusRoot.SetActive(has);
            else if (statusText) statusText.gameObject.SetActive(has);
        }
    }
}
