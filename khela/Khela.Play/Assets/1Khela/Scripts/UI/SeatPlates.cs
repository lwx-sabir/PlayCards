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

        [Header("Preview (dev only — ignored in a release build)")]
        [Tooltip("Show EMPTY seats as FULL placeholder cards — avatar + the authored name / chips / icon — instead of a " +
                 "blank frame, so you can eyeball the layout with no real players. Editor / dev builds only.")]
        [SerializeField] private bool previewShowPlaceholders;

        // Honoured in the editor + development builds only, so a forgotten toggle can never fake occupants in a shipped game.
        private bool PreviewPlaceholders => previewShowPlaceholders && (Application.isEditor || Debug.isDebugBuild);

        // Round-end HOLD: a registered RoundEndDirector presents the payout (reveal → draws → collect → pay). The
        // seat's chips show p.Balance, which the server has ALREADY credited on the settle push — so without a hold the
        // chip count jumps before the dealer pays. Latch on the in-round → settle transition and ignore pushes until the
        // director calls RevealNow (its PAY beat). Held as a MonoBehaviour so there's no hard type dependency on the
        // director (mirrors BetStacks). With NO director this is inert and plates update at settle exactly as before.
        private MonoBehaviour _settleDirector;
        private bool _held;
        private bool _prevInRound;

        /// <summary>The director arms the balance hold (called at its OnEnable).</summary>
        public void RegisterSettleDirector(MonoBehaviour director) => _settleDirector = director;
        public void UnregisterSettleDirector(MonoBehaviour director)
        { if (_settleDirector == director) { _settleDirector = null; _held = false; } }

        /// <summary>Director's PAY beat: drop the hold and render the credited balances now.</summary>
        public void RevealNow(BoardSnapshot board)
        {
            _held = false;
            Render(board ?? (table != null ? table.Board : null));
        }

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
            bool inRound = board.RoundInProgress;

            // Self-heal: a NEW round (inRound) is unambiguously past any round-end — drop a stuck hold. Guards the rare
            // reconnect-at-settle case where the director reseeds past the transition and never calls RevealNow.
            if (_held && inRound) _held = false;
            if (_held) return;   // director owns the reveal during the settle window

            // Latch on the in-round → settle transition when a director will present the payout.
            if (_settleDirector != null && _prevInRound && !inRound) { _held = true; _prevInRound = false; return; }
            _prevInRound = inRound;

            Render(board);
        }

        private void Render(BoardSnapshot board)
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
                else if (PreviewPlaceholders) plate.ShowPlaceholder();              // dev preview: full card, authored name/chips
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
