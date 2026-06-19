using Khela.Common.Blackjack;
using PlayCard.App;
using PlayCard.Game.Net;
using PlayCard.Home;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One table in the lobby carousel — a self-contained prefab (3D table + world-space info) bound to a
    /// <see cref="BlackjackTableSummary"/>. As an <see cref="ICarouselItem"/> it can show focus-only buttons,
    /// but the lobby also drives it from a shared HUD <b>Join</b> (joins the centred card via <see cref="Join"/>).
    /// Joining is server-authoritative (the request balance is ignored) and opens the Table scene.
    /// </summary>
    public sealed class LobbyTableCard : MonoBehaviour, ICarouselItem
    {
        [Header("Card UI (all optional)")]
        [SerializeField] private TMP_Text betText;        // "10k / 100k"
        [SerializeField] private TMP_Text playersText;    // "2/5"
        [SerializeField] private TMP_Text statusText;     // "Open" / "In play"

        [Header("Per-card buttons (optional, focus-only)")]
        [SerializeField] private Button playButton;
        [SerializeField] private Button viewButton;

        private string _tableId;
        private bool _canJoin;
        private bool _joining;

        public Transform Transform => transform;
        public string TableId => _tableId;
        public bool CanJoin => _canJoin;
        public string BetLabel { get; private set; }     // for the HUD's centre stake text
        public string PlayersLabel { get; private set; } // for the HUD's "Members 2/5"

        private void Awake()
        {
            if (playButton) playButton.onClick.AddListener(Join);
            if (viewButton) viewButton.onClick.AddListener(View);
            SetSelected(false);
        }

        private void OnDestroy()
        {
            if (playButton) playButton.onClick.RemoveListener(Join);
            if (viewButton) viewButton.onClick.RemoveListener(View);
        }

        /// <summary>Fill the card from a server table summary.</summary>
        public void Bind(BlackjackTableSummary s)
        {
            _tableId = s.TableId;
            _canJoin = s.SeatsOccupied < s.MaxPlayers;
            BetLabel = $"{Short(s.MinBet)} / {Short(s.MaxBet)}";
            PlayersLabel = $"{s.SeatsOccupied}/{s.MaxPlayers}";

            if (betText) betText.text = BetLabel;
            if (playersText) playersText.text = PlayersLabel;
            if (statusText) statusText.text = s.RoundInProgress ? "In play" : "Open";
            if (playButton) playButton.interactable = _canJoin;
        }

        public void SetSelected(bool selected)
        {
            if (playButton) playButton.gameObject.SetActive(selected);
            if (viewButton) viewButton.gameObject.SetActive(selected);
        }

        /// <summary>Seat the player here (server-authoritative) and open the Table scene. Public so a shared
        /// HUD Join button can drive the centred card.</summary>
        public async void Join()
        {
            if (_joining || !_canJoin || string.IsNullOrEmpty(_tableId)) return;
            _joining = true;
            if (statusText) statusText.text = "Joining…";

            var res = await BlackjackRestClient.Instance.JoinAsync(_tableId, "Player");
            if (res.Ok)
            {
                SceneNavigator.GoToTable(_tableId);
            }
            else
            {
                if (statusText) statusText.text = $"Join failed: {res.Error}";
                _joining = false;
            }
        }

        private void View()
        {
            // Spectate is a later feature; for now View opens the table scene without seating.
            if (!string.IsNullOrEmpty(_tableId)) SceneNavigator.GoToTable(_tableId);
        }

        // 12000 -> "12k", 1500000 -> "1.5M"
        private static string Short(decimal v)
        {
            if (v >= 1_000_000m) return $"{v / 1_000_000m:0.##}M";
            if (v >= 1_000m) return $"{v / 1_000m:0.##}k";
            return v.ToString("0");
        }
    }
}
