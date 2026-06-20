using PlayCard.Game.Dtos;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One seat's banner card (avatar + name + chips) at a FIXED position on the HUD. <see cref="SeatPlates"/> is
    /// the ONLY thing that controls its visibility: it populates the card with that seat's player, shows an empty
    /// placeholder, or hides it. The card does NOT touch its own active state in Awake — that raced with the
    /// driver under the per-seat layout toggle (driver showed it, then the card's Awake hid it).
    /// </summary>
    public sealed class SeatPlate : MonoBehaviour
    {
        [Tooltip("1-based seat number this card belongs to.")]
        [SerializeField] private int seatNumber = 1;
        [Tooltip("Visual to show/hide. Defaults to this GameObject.")]
        [SerializeField] private GameObject content;

        [Header("Fields")]
        [SerializeField] private Image avatar;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text chipsText;
        [Tooltip("The chip/coin icon next to the amount — hidden together with name + chips when the seat is empty.")]
        [SerializeField] private GameObject chipsIcon;
        [SerializeField] private string chipsFormat = "#,0";

        public int SeatNumber => seatNumber;

        // Lazy so it's valid even if a parent driver calls Show/Hide before this card's Awake runs.
        private GameObject Content => content != null ? content : gameObject;

        /// <summary>Occupied seat: show the player's name + chips.</summary>
        public void Show(PlayerView p)
        {
            if (p == null) { Hide(); return; }
            Content.SetActive(true);
            SetInfoVisible(true);
            if (nameText) nameText.text = p.Name;
            if (chipsText) chipsText.text = p.Balance.ToString(chipsFormat);
        }

        /// <summary>Empty-seat placeholder: default frame/avatar, name + chips hidden.</summary>
        public void ShowEmpty()
        {
            Content.SetActive(true);
            SetInfoVisible(false);
        }

        /// <summary>Hide the whole card.</summary>
        public void Hide()
        {
            Content.SetActive(false);
        }

        /// <summary>Drop in the profile sprite once available (FB/chosen).</summary>
        public void SetAvatar(Sprite sprite)
        {
            if (avatar && sprite) avatar.sprite = sprite;
        }

        private void SetInfoVisible(bool visible)
        {
            if (nameText != null) nameText.gameObject.SetActive(visible);
            if (chipsText != null) chipsText.gameObject.SetActive(visible);
            if (chipsIcon != null) chipsIcon.SetActive(visible);
        }
    }
}
