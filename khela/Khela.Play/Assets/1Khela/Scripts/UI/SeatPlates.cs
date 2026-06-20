using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Drives the seat banner cards from the live board. The logic is simple: HIDE the card for the LOCAL player's
    /// own seat (you're the bottom HUD), and in every other seat's card show its occupant (or an empty
    /// placeholder). Each <see cref="SeatPlate"/> handles staying glued to its chair as the camera moves.
    /// </summary>
    public sealed class SeatPlates : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("One card per seat, each with its Seat Number set.")]
        [SerializeField] private SeatPlate[] plates;
        [Tooltip("Hide the card for the local player's own seat (you're already shown by the bottom HUD).")]
        [SerializeField] private bool hideLocalSeat = true;
        [Tooltip("ON: a seat with no player is hidden. OFF: it shows the default card (frame/avatar) with the " +
                 "name + chips hidden — an 'empty seat' placeholder.")]
        [SerializeField] private bool hideEmptySeatCard;

        private void OnEnable()
        {
            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (board == null || plates == null) return;
            int mySeat = table != null ? table.MySeat : -1;

            foreach (var plate in plates)
            {
                if (plate == null) continue;

                // Your own seat → no card (you're the bottom HUD).
                if (hideLocalSeat && plate.SeatNumber == mySeat) { plate.Hide(); continue; }

                var seat = FindSeat(board, plate.SeatNumber);
                if (seat != null && seat.Player != null) plate.Show(seat.Player);   // someone's sitting there
                else if (hideEmptySeatCard) plate.Hide();                           // empty + hide
                else plate.ShowEmpty();                                             // empty + show default card
            }
        }

        private static SeatView FindSeat(BoardSnapshot board, int seatNumber)
        {
            if (board.Seats == null) return null;
            foreach (var s in board.Seats)
                if (s.SeatNumber == seatNumber) return s;
            return null;
        }
    }
}
