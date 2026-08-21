using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// "SIT HERE" for ONE seat at the table. Author one per real seat and point it at that seat's button.
    ///
    /// Why this exists: the join is issued by the LOBBY, and <see cref="TableController"/> deliberately skips joining
    /// when the scene was opened with a table id already set. Every other way into the table — Spectate/View, or a
    /// Play Now that carries a table id — therefore landed the player at a table with no seat and nothing anywhere in
    /// the scene that could seat them; Back was the only exit.
    ///
    /// Deliberately a SEPARATE, opt-in component rather than a change to <c>SeatPlate</c>/<c>SeatPlates</c>: those are
    /// shared by every seat's HUD and by the round-end director's payout hold, and adding a click surface to them
    /// would change all of that for a feature only the empty seats want.
    ///
    /// ⚠️ Put this on an ALWAYS-ACTIVE object and let it toggle <see cref="visual"/> (a CHILD). A component on a
    /// disabled GameObject never runs, so a script that switches itself off can never switch itself back on.
    /// </summary>
    public sealed class SeatSitButton : MonoBehaviour
    {
        [Tooltip("Auto-found if left empty.")]
        [SerializeField] private TableController table;

        [Tooltip("Server seat number this button sits in (1-based). Only author buttons for seats the server actually " +
                 "puts in play — Blackjack:PlayableSeats on the server, 3 today.")]
        [SerializeField] private int seatNumber = 1;

        [Tooltip("The button that takes the seat. Its interactable state is driven here.")]
        [SerializeField] private Button button;

        [Tooltip("Shown only while this seat is free and we are not seated. MUST be a CHILD, never this object — see " +
                 "the class note.")]
        [SerializeField] private GameObject visual;

        private bool _shown;
        private bool _wired;

        private void OnEnable()
        {
            if (table == null) table = FindAnyObjectByType<TableController>(FindObjectsInactive.Include);
            if (button != null && !_wired) { button.onClick.AddListener(Sit); _wired = true; }
            Apply(false);   // never flash a SIT prompt before the first board says the seat is free
        }

        private void OnDisable()
        {
            if (button != null && _wired) { button.onClick.RemoveListener(Sit); _wired = false; }
        }

        // Polled rather than board-driven on purpose: Sitting flips on the local tap with NO board push behind it, so
        // an OnBoardChanged-only watcher would leave the button live through the whole round trip. The guards below
        // are cheap and Apply only touches Unity when something actually changed.
        private void Update()
        {
            var board = table != null ? table.Board : null;
            Apply(board != null && !table.AmISeated && SeatIsFree(board));
            if (button != null && _shown) button.interactable = !table.Sitting;
        }

        /// <summary>The seat exists on this board and nobody is in it. Read off the seat list rather than
        /// <c>MaxPlayers</c>: the board reports the table's raw capacity, which can exceed the seats the server
        /// actually puts in play.</summary>
        private bool SeatIsFree(BoardSnapshot board)
        {
            if (board.Seats == null) return false;
            foreach (var s in board.Seats)
                if (s != null && s.SeatNumber == seatNumber)
                    return !s.Occupied && s.Player == null;
            return false;
        }

        private void Apply(bool show)
        {
            if (show == _shown) return;
            _shown = show;
            if (visual != null) visual.SetActive(show);
            if (button != null) button.interactable = show;
        }

        private async void Sit()
        {
            if (table == null || table.Sitting || table.AmISeated) return;
            // Grey it on the tap frame. The request is a round trip and the board will not have moved yet, so without
            // this a second tap fires a second join — which the server answers with "Seat N is already taken".
            if (button != null) button.interactable = false;
            await table.SitAsync(seatNumber);
        }
    }
}
