using PlayCard.Game.Dtos;
using TMPro;
using UnityEngine;

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

        [Header("Occupied / empty views")]
        [Tooltip("Everything shown for a SEATED player — avatar, name, balance. Assign Opponent_Banner. Cross-toggled " +
                 "with Empty View, so exactly one of the two is ever on while the card is visible.")]
        [SerializeField] private GameObject occupiedView;

        [Tooltip("Shown when the chair is EMPTY (no name, no avatar, no chips to show). Assign Empty_Seat. Leave both " +
                 "of these empty on an older banner and the card falls back to just hiding the name/chips fields.")]
        [SerializeField] private GameObject emptyView;

        [Tooltip("Win celebration particle. Left OFF by default and switched on only when this seat WINS, at the same " +
                 "beat the win badge appears — then off again when the next round deals.")]
        [SerializeField] private GameObject winFx;

        [Header("Fields")]
        [Tooltip("The avatar ELEMENT (portrait + state frames) — assign the object carrying SeatAvatar, e.g. " +
                 "Player_Avatar. Not a bare Image: the avatar is a composite now, so the plate hands work to the " +
                 "element and stays out of how the art is assembled.")]
        [SerializeField] private SeatAvatar avatar;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text chipsText;
        [Tooltip("The chip/coin icon next to the amount — hidden together with name + chips when the seat is empty.")]
        [SerializeField] private GameObject chipsIcon;
        [SerializeField] private string chipsFormat = "#,0";

        public int SeatNumber => seatNumber;

        // Lazy so it's valid even if a parent driver calls Show/Hide before this card's Awake runs.
        private GameObject Content => content != null ? content : gameObject;

        /// <summary>
        /// Occupied seat: show the player's name + chips.
        ///
        /// <paramref name="fallbackPortrait"/> is used when the player has no avatar of their own. Right now that is
        /// EVERY player — the board's <see cref="PlayerView"/> carries no avatar data at all (Id / Name / Balance /
        /// seat / hands / stats), so until the server sends one this is what every other seat shows. Passing null
        /// leaves the banner prefab's authored art untouched.
        /// </summary>
        public void Show(PlayerView p, Sprite fallbackPortrait = null)
        {
            if (p == null) { Hide(); return; }
            Content.SetActive(true);
            SetOccupied(true);
            if (nameText) nameText.text = p.Name;

            // No per-player portrait exists yet, so the fallback is the portrait. When PlayerView starts carrying an
            // avatar, resolve that FIRST here and keep this as the "they have none" branch.
            if (fallbackPortrait != null && avatar != null) avatar.SetPortrait(fallbackPortrait);
            // Skip a chips label a ChipCountJuice is animating — writing the board value here would overwrite the roll
            // mid-count and make the number appear to jump straight to the settled balance.
            if (chipsText && !ChipCountJuice.Owns(chipsText)) chipsText.text = p.Balance.ToString(chipsFormat);
        }

        /// <summary>Nobody in this chair: swap to the empty-seat view (no name, no avatar, no chips).</summary>
        public void ShowEmpty()
        {
            Content.SetActive(true);
            SetOccupied(false);
        }

        /// <summary>Dev preview: show the FULL card with its AUTHORED placeholder name / chips / icon (kept as-is, not
        /// overwritten), so an empty seat can be eyeballed with all elements while there's no real player.</summary>
        public void ShowPlaceholder()
        {
            Content.SetActive(true);
            SetOccupied(true);
        }

        /// <summary>Hide the whole card.</summary>
        public void Hide()
        {
            SetWinFx(false);   // a hidden card must not leave particles playing over the felt
            Content.SetActive(false);
        }

        /// <summary>The avatar element, for callers that want its frame states directly. Null if unassigned.</summary>
        public SeatAvatar Avatar => avatar;

        /// <summary>Drop in the profile sprite once available (FB/chosen).</summary>
        public void SetAvatar(Sprite sprite)
        {
            if (avatar != null) avatar.SetPortrait(sprite);
        }

        /// <summary>Set this seat's avatar frame (idle / acting / winner). Nothing drives this yet — it's here so the
        /// frames can be wired without reaching into the element's children from outside.</summary>
        public void SetAvatarState(SeatAvatar.State state)
        {
            if (avatar != null) avatar.SetState(state);
        }

        /// <summary>
        /// Show or hide this seat's win celebration. Driven by the round-end director's PAY beat so it lands with the
        /// win badge and the chips, NOT on the settle push — the server flips the round over the instant it resolves,
        /// which is before the dealer has even revealed.
        /// </summary>
        public void SetWinFx(bool on)
        {
            if (winFx != null && winFx.activeSelf != on) winFx.SetActive(on);
        }

        /// <summary>Arm this seat's turn ring. Null deadline = not this seat's turn.</summary>
        public void SetTurn(System.DateTimeOffset? endsAt, float turnSeconds)
        {
            if (avatar != null) avatar.SetTurn(endsAt, turnSeconds);
        }

        /// <summary>
        /// Cross-toggle the two views: exactly one is on whenever the card itself is visible. Kept in ONE place so a
        /// path can never leave both on (an empty-seat graphic stacked under a player's name) or both off (a blank
        /// frame that looks like a rendering bug).
        /// </summary>
        private void SetOccupied(bool occupied)
        {
            if (occupiedView != null) occupiedView.SetActive(occupied);
            if (emptyView != null) emptyView.SetActive(!occupied);

            // Fallback for a banner authored WITHOUT the two view groups: behave exactly as before and just show or
            // hide the individual fields. Only used when Occupied View is unassigned, so wiring the groups is what
            // switches a prefab over — no flag to remember.
            if (occupiedView == null) SetInfoVisible(occupied);
        }

        private void SetInfoVisible(bool visible)
        {
            if (nameText != null) nameText.gameObject.SetActive(visible);
            if (chipsText != null) chipsText.gameObject.SetActive(visible);
            if (chipsIcon != null) chipsIcon.SetActive(visible);
        }
    }
}
