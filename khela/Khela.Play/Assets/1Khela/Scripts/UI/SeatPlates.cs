using System;
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
        [Tooltip("The table view — supplies DecisionReady so a seat's turn ring waits for the deal to land instead of " +
                 "lighting the instant the server hands out the turn. AUTO-FOUND: left null it would silently skip " +
                 "the gate, which is the whole bug it exists to prevent.")]
        [SerializeField] private BlackjackTableView view;
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

        /// <summary>
        /// Light the win particle on every seat that WON. Driven by the director's HOLD beat — the same moment the
        /// WIN badge is revealed on the felt — never by the board, which flips the round over the instant it resolves
        /// and would celebrate over a face-down hole card. Cleared once the payout has finished.
        /// </summary>
        public void ShowWinFx(BoardSnapshot board) => ApplyWinFx(board ?? (table != null ? table.Board : null));

        private void ApplyWinFx(BoardSnapshot board)
        {
            if (plates == null) return;

            foreach (var plate in plates)
            {
                if (plate == null) continue;

                bool won = false;
                if (board?.LastResults != null)
                {
                    foreach (var r in board.LastResults)
                    {
                        if (r == null || r.SeatNumber != plate.SeatNumber) continue;
                        // Outcome is the seat's NET across all its hands, so a split that nets a win celebrates once
                        // rather than per hand. Push and lose stay silent.
                        won = string.Equals(r.Outcome, "win", StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                }

                plate.SetWinFx(won);
            }
        }

        /// <summary>Stop the celebration — the director calls this once the payout chips have landed and the count
        /// has finished rolling, immediately before the felt is swept.</summary>
        public void ClearWinFx()
        {
            if (plates == null) return;
            foreach (var plate in plates)
                if (plate != null) plate.SetWinFx(false);
        }

        // The shared "player has no avatar" portrait lives ONCE on the panel that owns every seat layout
        // (BettingAvatarFader), so all three layouts and all six banners resolve the same reference instead of each
        // carrying a copy. Cached here rather than looked up per render. includeInactive: the panel can legitimately
        // be faded/disabled when a layout first enables.
        private BettingAvatarFader _panel;
        private Sprite DefaultPortrait => _panel != null ? _panel.DefaultAvatar : null;

        private void OnEnable()
        {
            if (_panel == null) _panel = GetComponentInParent<BettingAvatarFader>(true);
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>(FindObjectsInactive.Include);

            // Blank FIRST, before anything can be shown. Render is the only thing that ever calls Show/Hide on a
            // plate, and it only runs off a board — so until the first snapshot lands the cards keep whatever the
            // SCENE authored, which is dummy content: a placeholder portrait and the sample name and chip count used
            // to size the layout. On a fast connection the board arrives in the same breath and nobody sees it; on a
            // real device with a slow first response that is four to seven seconds of a table showing invented
            // players with invented balances, which is a far worse thing to show than nothing.
            //
            // An empty card is also the honest state: before the first board we genuinely do not know who is seated.
            BlankUntilFirstBoard();

            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);
        }

        /// <summary>Hide every plate until a board says otherwise. Skipped in the dev placeholder preview.</summary>
        private void BlankUntilFirstBoard()
        {
            if (plates == null || PreviewPlaceholders) return;
            foreach (var plate in plates)
                if (plate != null) plate.Hide();
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
            if (_settleDirector != null && _prevInRound && !inRound)
            {
                _held = true;
                _prevInRound = false;
                // The hold skips Render for the whole payout ceremony, so the seat that acted last would keep its
                // playing frame and a drained ring the entire time. Nobody is on the clock once the round is over.
                foreach (var p in plates)
                {
                    if (p == null) continue;
                    p.SetAvatarState(SeatAvatar.State.Idle);
                    p.SetTurn(null, 0f);
                }
                return;
            }
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
                if (seat != null && seat.Player != null) plate.Show(seat.Player, DefaultPortrait);   // someone's sitting there
                else if (PreviewPlaceholders) plate.ShowPlaceholder();              // dev preview: full card, authored name/chips
                else if (hideEmptySeatCard) plate.Hide();                           // empty + hide
                else plate.ShowEmpty();                                             // empty + show default card
            }

            ApplyTurnFrames(board);
        }

        /// <summary>
        /// Light the countdown ring on the seat that is ACTING, and only once it really can act.
        ///
        /// The server hands out the turn in the SAME board that starts the deal, so keying straight off
        /// <c>CurrentSeatNumber</c> lit the ring while the dealer was still throwing cards — and if a remote player
        /// had first turn, their ring appeared the instant the round began. <c>DecisionReady</c> (that seat's cards
        /// AND the dealer's have landed) is the same gate the turn glow and the action bar use, so the ring starts
        /// when the clock really does.
        /// </summary>
        private void ApplyTurnFrames(BoardSnapshot board)
        {
            if (board == null || plates == null) return;

            int acting = board.RoundInProgress ? board.CurrentSeatNumber : -1;
            if (acting > 0 && view != null && !view.DecisionReady(acting)) acting = -1;

            // BETWEEN rounds the same ring becomes each seat's BET clock: every seated player who hasn't staked yet
            // counts down the shared window, and their ring clears the moment they bet. So a seat is "on the clock"
            // either because it's their turn to act, or because the table is waiting on their bet.
            // …but NOT during the round-end ceremony. The server flips RoundInProgress false and arms the next betting
            // window the instant a hand resolves — a stand or a bust — so keying on that alone re-lit the ring at 100%
            // on the player who just acted, which reads as "the ring never cleared". RoundEndSettling is the same
            // guard the bet bar, chip rail and bet-spot glow use for exactly this.
            bool bettingWindow = !board.RoundInProgress
                                 && board.BettingExpiresAt.HasValue
                                 && !(view != null && view.RoundEndSettling);

            foreach (var plate in plates)
            {
                if (plate == null) continue;

                bool onClock;
                DateTimeOffset? until;
                float seconds;

                if (bettingWindow)
                {
                    var seat = FindSeat(board, plate.SeatNumber);
                    // Occupied, and hasn't bet this window. BetThisWindow is the server's explicit "they staked"
                    // flag — don't infer it from Bet > 0, which is also true of a settled hand still on the felt.
                    onClock = seat?.Player != null && !seat.Player.BetThisWindow;
                    until = board.BettingExpiresAt;
                    seconds = board.BettingDurationSeconds;
                }
                else
                {
                    onClock = acting > 0 && plate.SeatNumber == acting;
                    until = board.TurnExpiresAt;
                    seconds = board.TurnDurationSeconds;
                }

                plate.SetAvatarState(onClock ? SeatAvatar.State.Playing : SeatAvatar.State.Idle);
                plate.SetTurn(onClock ? until : null, seconds);
            }
        }

        // The deal LANDS between board pushes, so DecisionReady flips with no message to react to — the same reason
        // the action bar and the turn glow poll. Change-guarded, so this costs a couple of comparisons per frame.
        private int _lastActing = int.MinValue;
        private bool _lastReady;
        private bool _lastSettling;

        private void Update()
        {
            var board = table != null ? table.Board : null;
            if (board == null || _held) return;

            int seat = board.RoundInProgress ? board.CurrentSeatNumber : -1;
            bool ready = seat <= 0 || view == null || view.DecisionReady(seat);
            // The ceremony ends between board pushes, and it gates the betting ring — without watching it the rings
            // wouldn't arm until the server next spoke.
            bool settling = view != null && view.RoundEndSettling;
            if (seat == _lastActing && ready == _lastReady && settling == _lastSettling) return;

            _lastActing = seat;
            _lastReady = ready;
            _lastSettling = settling;
            ApplyTurnFrames(board);
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
