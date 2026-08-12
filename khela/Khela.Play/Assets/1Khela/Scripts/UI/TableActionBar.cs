using PlayCard.Game.Betting;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The in-table action bar: Deal/Hit/Stand/Double/Split/Insurance/DealerPlay/Leave, gated off the live board
    /// from <see cref="TableController"/>. There is NO typed bet entry — the wager is built by dropping chips
    /// (<see cref="BetBuilder"/> sums them on the bet spot) and DEAL sends that running total + deals in one tap.
    /// The server still waits for the other seated players before the round runs. Gating is UX only (the server
    /// re-validates every action); every field is optional (null-tolerant) so the Canvas can be wired incrementally.
    /// </summary>
    public sealed class TableActionBar : MonoBehaviour
    {
        [Header("Controller")]
        [SerializeField] private TableController table;
        [Tooltip("Chip-bet accumulator. DEAL places its running total, then deals.")]
        [SerializeField] private BetBuilder betBuilder;
        [Tooltip("The table view — holds Hit/Stand/… until the animated deal (or the last action's card) has landed. Optional.")]
        [SerializeField] private BlackjackTableView view;

        [Header("Betting")]
        [SerializeField] private Button dealButton;
        [Tooltip("Re-drops your last bet's chips and deals in one tap. Needs the BetRepeater.")]
        [SerializeField] private Button repeatButton;
        [Tooltip("Optional: the REPEAT button's label — shown as \"REPEAT 100K\" with the last bet amount.")]
        [SerializeField] private TMP_Text repeatLabel;
        [Tooltip("Clears the chip stack and zeroes the running bet.")]
        [SerializeField] private Button clearButton;
        [SerializeField] private BetRepeater betRepeater;

        [Header("Actions")]
        [SerializeField] private Button hitButton;
        [SerializeField] private Button standButton;
        [SerializeField] private Button doubleButton;
        [SerializeField] private Button splitButton;
        [SerializeField] private Button insuranceButton;
        [SerializeField] private Button dealerPlayButton;
        [SerializeField] private Button leaveButton;

        [Header("Feedback")]
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text errorText;

        private void Awake()
        {
            Wire(dealButton, Deal);
            Wire(repeatButton, Repeat);
            Wire(clearButton, ClearBet);
            Wire(hitButton, () => _ = table.Hit());
            Wire(standButton, () => _ = table.Stand());
            Wire(doubleButton, () => _ = table.DoubleDown());
            Wire(splitButton, () => _ = table.Split());
            Wire(insuranceButton, PlaceInsurance);
            Wire(dealerPlayButton, () => _ = table.DealerPlay());
            Wire(leaveButton, () => _ = table.Leave());
        }

        private void OnEnable()
        {
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();   // so the deal-landed gate can't be silently bypassed
            if (table != null)
            {
                table.OnBoardChanged += Refresh;
                table.OnActionError += ShowError;
            }
            if (betBuilder != null) betBuilder.OnBetChanged += OnBetChanged;
            Refresh(table != null ? table.Board : null);
        }

        private void OnDisable()
        {
            if (table != null)
            {
                table.OnBoardChanged -= Refresh;
                table.OnActionError -= ShowError;
            }
            if (betBuilder != null) betBuilder.OnBetChanged -= OnBetChanged;
        }

        // DEAL = place the chip bet + deal for this player, in one tap. The chips you dropped are the amount; the
        // server keeps the round in the betting phase until the other seated players have dealt too.
        private void Deal()
        {
            ClearError();
            if (betBuilder == null) return;
            if (!betBuilder.MeetsMinimum) { ShowError("Drop chips to set your bet."); return; }
            betBuilder.Deal();   // PlaceBet(running total) → Deal
        }

        // REPEAT = re-drop the exact chips from your last bet onto the spot (physics), then deal — no rebuild.
        private void Repeat()
        {
            ClearError();
            if (betRepeater != null) betRepeater.Repeat();
        }

        // CLEAR = wipe the chip stack and zero the running bet.
        private void ClearBet()
        {
            ClearError();
            if (betBuilder != null) betBuilder.Clear();
        }

        private void PlaceInsurance()
        {
            var hand = MyCurrentHand();
            if (hand != null) _ = table.Insurance(hand.Bet / 2m);
        }

        // Re-gate DEAL as chips are dropped/cleared (MeetsMinimum changes off-board).
        private void OnBetChanged(decimal total) => Refresh(table != null ? table.Board : null);

        private void Refresh(BoardSnapshot board)
        {
            if (table == null) return;

            bool seated = table.MySeat > 0;
            bool inRound = board != null && board.RoundInProgress;
            // The round-end ceremony (reveal → collect → pay → sweep) still owns the felt. A blackjack settles the round
            // INSTANTLY, so inRound flips false while the ceremony is only starting — without this the bet bar would pop
            // up over the payout. Hold ALL controls until the director has finished.
            bool settling = view != null && view.RoundEndSettling;
            bool myTurn = table.IsMyTurn;
            var hand = MyCurrentHand();

            // BettingOpenForMe, not !inRound: the betting window is SHARED, so RoundInProgress stays false until the
            // last seat bets — DEAL/REPEAT/CLEAR stayed live for a player who had already committed, and pressing them
            // again is exactly the double-deal the server then rejects.
            bool canBet = table.BettingOpenForMe && !settling && seated;   // and the previous round-end fully done
            // DEAL is live only when seated, between rounds, and the dropped chips meet the table minimum.
            Set(dealButton, canBet && betBuilder != null && betBuilder.MeetsMinimum);
            // REPEAT is live between rounds once there's a remembered bet; CLEAR whenever there are chips down.
            Set(repeatButton, canBet && betRepeater != null && betRepeater.CanRepeat);
            Set(clearButton, canBet && betBuilder != null && betBuilder.Total > 0m);

            if (repeatLabel != null)
            {
                long last = betBuilder != null ? betBuilder.LastBet : 0;
                repeatLabel.text = last > 0 ? $"REPEAT {ChipView.Format(last)}" : "REPEAT";
            }

            // Hold the action buttons until MY cards AND the DEALER's have landed (the dealer is dealt last, so that's
            // the whole deal finished) — but NOT other players' seats, so a remote hit can't eat my turn timer. Also OFF
            // during the round-end ceremony (a blackjack can leave a stale my-turn flag as it auto-resolves).
            bool act = myTurn && !settling && (view == null || view.DecisionReady(table.MySeat));
            Set(hitButton, act);
            Set(standButton, act);
            Set(doubleButton, act && hand != null && hand.Cards.Count == 2);
            Set(splitButton, act && CanSplit(hand));
            Set(insuranceButton, act && hand != null && hand.Insurance == 0 && DealerShowsAce(board));

            // The server round-driver auto-settles ~2s after everyone has acted (and auto-stands a player whose
            // turn timer expired). This Dealer Play button is an optional "settle now" shortcut, shown once all
            // hands are resolved.
            Set(dealerPlayButton, inRound && board != null && board.CurrentSeatNumber == -1);
            Set(leaveButton, true);

            if (statusText != null) statusText.text = BuildStatus(board);
        }

        private bool _lastAnimating;
        private bool _lastSettling;
        private bool _lastCommitted;   // betting commit flips on the button press, between board pushes

        private void Update()
        {
            if (table == null) return;
            // Re-render each frame so the countdown actually ticks — during a round that's the turn clock, between
            // rounds it's the betting window (the server auto-deals when that expires, so it has to be visible).
            bool ticking = table.Board != null
                           && (table.Board.RoundInProgress || table.Board.BettingExpiresAt.HasValue);
            if (ticking && statusText != null)
                statusText.text = BuildStatus(table.Board);

            // Cards land BETWEEN board pushes, so re-gate the buttons when my decision inputs (my cards + the dealer's)
            // settle — otherwise Refresh (board-driven) would leave Hit/Stand a beat behind the dealt cards. The round-end
            // ceremony ALSO ends between pushes (the director drops its hold, no board fires), so watch RoundEndSettling
            // too — else the bet bar would stay dark until the next server push.
            // Committing a bet is a LOCAL flip on the button press — no board arrives to trigger Refresh, and during a
            // shared betting window the next push can be seconds away, so without this the bar stays live long after
            // the player has bet.
            bool localUnsettled = view != null && !view.DecisionReady(table.MySeat);
            bool settling = view != null && view.RoundEndSettling;
            bool committed = table.BettingCommitted;
            if (localUnsettled != _lastAnimating || settling != _lastSettling || committed != _lastCommitted)
            {
                _lastAnimating = localUnsettled;
                _lastSettling = settling;
                _lastCommitted = committed;
                Refresh(table.Board);
            }
        }

        // Human-readable state for testing: bet phase, whose turn (+ countdown), dealer, and an end-of-round
        // line showing both totals. The authoritative win/loss is the chips delta (bind a BalanceHud); a real
        // result banner comes once HandResult/Payout is added to the board.
        private string BuildStatus(BoardSnapshot board)
        {
            if (table.MySeat <= 0) return "Spectating";
            if (board == null) return "Connecting…";

            var hand = MyCurrentHand();
            int myVal = hand?.HandValue ?? 0;
            int dealerVal = board.Dealer?.HandValue ?? 0;

            if (!board.RoundInProgress)
            {
                // BETTING WINDOW countdown — computed BEFORE the round-over early-out. The server leaves the settled
                // hands on the felt until the next deal, so "hasCards" is true for the WHOLE between-rounds window;
                // returning "Round over" first therefore made this unreachable in every window after round one, which
                // is exactly the window the countdown exists for. Both lines carry it now.
                string betTimer = string.Empty;
                if (board.BettingExpiresAt.HasValue && (view == null || !view.RoundEndSettling))
                {
                    double left = (board.BettingExpiresAt.Value - System.DateTimeOffset.UtcNow).TotalSeconds;
                    if (board.BettingDurationSeconds > 0) left = System.Math.Min(left, board.BettingDurationSeconds);
                    if (left > 0) betTimer = $" — betting closes in {left:0}s";
                }

                bool hasCards = hand?.Cards != null && hand.Cards.Count > 0;
                if (hasCards) return $"Round over — you {myVal} / dealer {dealerVal}{betTimer}";
                return $"Place your bet{betTimer}";
            }

            string timer = string.Empty;
            // Don't tick the turn clock while the deal is still animating in — show it only once MY cards + the dealer's
            // have LANDED (DecisionReady), so the countdown starts when I can actually act, not mid-deal. (The server
            // also grants a deal-presentation buffer so the shown time starts near the full turn.)
            if (board.TurnExpiresAt.HasValue && (view == null || view.DecisionReady(table.MySeat)))
            {
                double remaining = (board.TurnExpiresAt.Value - System.DateTimeOffset.UtcNow).TotalSeconds;
                // CLAMP to a real turn: the deadline is a generous ceiling until our /presented call collapses it, so
                // without this we'd flash the ceiling for the round-trip (or forever, if that call ever fails).
                if (board.TurnDurationSeconds > 0) remaining = System.Math.Min(remaining, board.TurnDurationSeconds);
                if (remaining > 0) timer = $" ({remaining:0}s)";
            }

            if (board.CurrentSeatNumber == -1) return "Dealer playing…";
            if (table.IsMyTurn) return $"Your turn — hand {myVal}{timer}";
            return $"Seat {board.CurrentSeatNumber} playing…{timer}";
        }

        private HandView MyCurrentHand()
        {
            var board = table.Board;
            if (board?.Seats == null) return null;
            var me = board.Seats.Find(s => s.SeatNumber == table.MySeat)?.Player;
            if (me?.Hands == null || me.Hands.Count == 0) return null;
            int idx = Mathf.Clamp(board.CurrentHandIndex, 0, me.Hands.Count - 1);
            return me.Hands[idx];
        }

        // Mirror the server's CanSplitPair (Player.cs): a pair splits on equal BLACKJACK value — 10/J/Q/K all = 10,
        // Ace = 11 — NOT same rank, so K+Q is splittable. The server re-validates; this is the UX gate only.
        private static int SplitValue(int faceVal)
            => faceVal == 14 ? 11 : (faceVal >= 11 && faceVal <= 13 ? 10 : faceVal);   // Ace=14→11, J/Q/K→10, else pip

        private static bool CanSplit(HandView hand)
            => hand != null && hand.Cards != null && hand.Cards.Count == 2
               && SplitValue(hand.Cards[0].FaceVal) == SplitValue(hand.Cards[1].FaceVal);

        private static bool DealerShowsAce(BoardSnapshot board)
        {
            var cards = board?.Dealer?.Cards;
            if (cards == null) return false;
            foreach (var c in cards)
                if (c.IsCardUp && c.FaceVal == 14) return true; // Ace face value = 14
            return false;
        }

        private void ShowError(string msg) { if (errorText != null) errorText.text = msg; }
        private void ClearError() { if (errorText != null) errorText.text = string.Empty; }

        private static void Wire(Button b, UnityEngine.Events.UnityAction action)
        {
            if (b != null) b.onClick.AddListener(action);
        }

        private static void Set(Button b, bool on)
        {
            if (b != null) b.interactable = on;
        }
    }
}
