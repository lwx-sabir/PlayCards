using System;
using System.Collections.Generic;
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

        [Header("Per-card buttons (optional, focus-only)")]
        [SerializeField] private Button playButton;
        [SerializeField] private Button viewButton;

        [Header("Seat slots — the PlayerCard1..N on this table (tap an empty one to sit)")]
        [SerializeField] private LobbySeatCard[] seatCards;

        private string _tableId;
        private bool _canJoin;
        private bool _joining;
        private BlackjackTableSummary _summary;

        public Transform Transform => transform;
        public string TableId => _tableId;
        public bool CanJoin => _canJoin;
        public string BetLabel { get; private set; }     // for the HUD's centre stake text
        public string PlayersLabel { get; private set; } // for the HUD's "Members 2/5"

        /// <summary>Join/loading feedback for this card — the lobby HUD subscribes and drives its status/spinner.</summary>
        public event Action<string> OnStatus;

        /// <summary>Seated players on this table (SeatNumber → name/image/balance) — to render the chairs.</summary>
        public IReadOnlyList<TableOccupant> Occupants => _summary?.Occupants;
        /// <summary>Total seats on this table.</summary>
        public int MaxPlayers => _summary?.MaxPlayers ?? 0;

        /// <summary>True if <paramref name="seatNumber"/> (1-based) is a real, empty seat — i.e. clickable to sit.</summary>
        public bool IsSeatOpen(int seatNumber)
        {
            if (_summary == null || seatNumber < 1 || seatNumber > _summary.MaxPlayers) return false;
            foreach (var o in _summary.Occupants)
                if (o.SeatNumber == seatNumber) return false;
            return true;
        }

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
            _summary = s;
            _tableId = s.TableId;
            _canJoin = s.SeatsOccupied < s.MaxPlayers;
            BetLabel = $"{Short(s.MinBet)} / {Short(s.MaxBet)}";
            PlayersLabel = $"{s.SeatsOccupied}/{s.MaxPlayers}";

            if (betText) betText.text = BetLabel;
            if (playersText) playersText.text = PlayersLabel;
            if (playButton) playButton.interactable = _canJoin;

            BindSeats();
        }

        // Drive each seat slot: occupied → show the player, open → show empty + tap to sit (JoinSeat).
        private void BindSeats()
        {
            if (seatCards == null) return;
            foreach (var sc in seatCards)
            {
                if (sc == null) continue;
                var occ = FindOccupant(sc.SeatNumber);
                if (occ != null) sc.ShowOccupant(occ);
                else sc.ShowEmpty(JoinSeat);
            }
        }

        private TableOccupant FindOccupant(int seatNumber)
        {
            if (_summary?.Occupants == null) return null;
            foreach (var o in _summary.Occupants)
                if (o.SeatNumber == seatNumber) return o;
            return null;
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
            SetStatus("Joining…");

            var res = await BlackjackRestClient.Instance.JoinAsync(_tableId, "Player");
            if (res.Ok)
            {
                SceneNavigator.GoToTable(_tableId);
            }
            else
            {
                SetStatus($"Join failed: {res.Error}");
                _joining = false;
            }
        }

        /// <summary>Seat the player at a specific (empty) seat, then open the Table scene — wire each empty
        /// chair's click to this with its seat number. No-op on a taken/invalid seat. Server-authoritative.</summary>
        public async void JoinSeat(int seatNumber)
        {
            if (_joining || string.IsNullOrEmpty(_tableId) || !IsSeatOpen(seatNumber)) return;
            _joining = true;
            SetStatus("Joining…");

            var res = await BlackjackRestClient.Instance.JoinAsync(_tableId, "Player", "", seatNumber);
            if (res.Ok)
            {
                SceneNavigator.GoToTable(_tableId, seatNumber);   // carry the chosen seat so the table resolves it instantly
            }
            else
            {
                SetStatus($"Join failed: {res.Error}");
                _joining = false;
            }
        }

        private void View()
        {
            // Spectate is a later feature; for now View opens the table scene without seating.
            if (!string.IsNullOrEmpty(_tableId)) SceneNavigator.GoToTable(_tableId);
        }

        // Join/loading feedback bubbles up to the lobby HUD's status/spinner (it lives there, not on the card).
        private void SetStatus(string s) => OnStatus?.Invoke(s);

        // 12000 -> "12k", 1500000 -> "1.5M"
        private static string Short(decimal v)
        {
            if (v >= 1_000_000m) return $"{v / 1_000_000m:0.##}M";
            if (v >= 1_000m) return $"{v / 1_000m:0.##}k";
            return v.ToString("0");
        }
    }
}
