using System;
using Khela.Common.Blackjack;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One seat slot on a lobby table card (PlayerCard1..N). Shows the seated player or an empty, tappable seat.
    /// <see cref="LobbyTableCard"/> binds every seat card each refresh: occupied → <see cref="ShowOccupant"/>,
    /// open → <see cref="ShowEmpty"/> (tapping seats you there and opens the table). The shared <b>Join</b>
    /// button (auto-seat) is separate and unchanged. All refs are optional — assign whatever your card has.
    /// </summary>
    public sealed class LobbySeatCard : MonoBehaviour
    {
        [Tooltip("1-based seat number this card represents (PlayerCard1 → 1, PlayerCard2 → 2, …).")]
        [SerializeField] private int seatNumber = 1;

        [Header("State roots (optional — toggled by occupied/empty)")]
        [SerializeField] private GameObject occupiedRoot;   // the seated-player art
        [SerializeField] private GameObject emptyRoot;      // the "empty seat / tap to sit" art

        [Header("Occupant fields (optional)")]
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text balanceText;

        [Header("Tap")]
        [Tooltip("Button that selects this seat. Its click is wired automatically.")]
        [SerializeField] private Button button;

        public int SeatNumber => seatNumber;

        private Action<int> _onTap;

        private void Awake()
        {
            if (button) button.onClick.AddListener(() => _onTap?.Invoke(seatNumber));
        }

        /// <summary>Open seat: show the empty art and enable the tap → seats the player here.</summary>
        public void ShowEmpty(Action<int> onTap)
        {
            _onTap = onTap;
            if (occupiedRoot) occupiedRoot.SetActive(false);
            if (emptyRoot) emptyRoot.SetActive(true);
            if (button) button.interactable = true;
        }

        /// <summary>Taken seat: show the occupant and disable the tap.</summary>
        public void ShowOccupant(TableOccupant o)
        {
            _onTap = null;
            if (occupiedRoot) occupiedRoot.SetActive(true);
            if (emptyRoot) emptyRoot.SetActive(false);
            if (button) button.interactable = false;
            if (nameText) nameText.text = o.Name;
            if (balanceText) balanceText.text = Short(o.Balance);
            // avatarImage: load from o.Image via your avatar pipeline if you have one.
        }

        private static string Short(decimal v)
        {
            if (v >= 1_000_000m) return $"{v / 1_000_000m:0.##}M";
            if (v >= 1_000m) return $"{v / 1_000m:0.##}k";
            return v.ToString("0");
        }
    }
}
