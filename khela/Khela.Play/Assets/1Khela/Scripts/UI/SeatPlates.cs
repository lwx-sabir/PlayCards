using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Drives the per-seat <see cref="SeatPlate"/>s from the live board: on every board update it shows/hides each
    /// plate and fills name + chips from that seat's occupant. Put on the HUD canvas; assign the TableController
    /// and the plates (one per seat). Each plate follows its own world anchor — this just binds data + visibility.
    /// </summary>
    public sealed class SeatPlates : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [SerializeField] private SeatPlate[] plates;
        [Tooltip("Hide the plate over the local player's own seat (you already have your own HUD at the bottom).")]
        [SerializeField] private bool hideLocalSeat;

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

                if (hideLocalSeat && plate.SeatNumber == mySeat) { plate.Hide(); continue; }

                var seat = FindSeat(board, plate.SeatNumber);
                if (seat != null && seat.Occupied && seat.Player != null) plate.Show(seat.Player);
                else plate.Hide();
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
