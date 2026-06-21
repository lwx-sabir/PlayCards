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
            bool myTurn = table.IsMyTurn;
            var hand = MyCurrentHand();

            // DEAL is live only when seated, between rounds, and the dropped chips meet the table minimum.
            Set(dealButton, !inRound && seated && betBuilder != null && betBuilder.MeetsMinimum);
            // REPEAT is live between rounds once there's a remembered bet; CLEAR whenever there are chips down.
            Set(repeatButton, !inRound && seated && betRepeater != null && betRepeater.CanRepeat);
            Set(clearButton, !inRound && seated && betBuilder != null && betBuilder.Total > 0m);

            if (repeatLabel != null)
            {
                long last = betBuilder != null ? betBuilder.LastBet : 0;
                repeatLabel.text = last > 0 ? $"REPEAT {ChipView.Format(last)}" : "REPEAT";
            }

            Set(hitButton, myTurn);
            Set(standButton, myTurn);
            Set(doubleButton, myTurn && hand != null && hand.Cards.Count == 2);
            Set(splitButton, myTurn && CanSplit(hand));
            Set(insuranceButton, myTurn && hand != null && hand.Insurance == 0 && DealerShowsAce(board));

            // The server round-driver auto-settles ~2s after everyone has acted (and auto-stands a player whose
            // turn timer expired). This Dealer Play button is an optional "settle now" shortcut, shown once all
            // hands are resolved.
            Set(dealerPlayButton, inRound && board != null && board.CurrentSeatNumber == -1);
            Set(leaveButton, true);

            if (statusText != null) statusText.text = BuildStatus(board);
        }

        private void Update()
        {
            // Re-render each frame during a round so the turn countdown actually ticks.
            if (table != null && table.Board != null && table.Board.RoundInProgress && statusText != null)
                statusText.text = BuildStatus(table.Board);
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
                bool hasCards = hand?.Cards != null && hand.Cards.Count > 0;
                return hasCards ? $"Round over — you {myVal} / dealer {dealerVal}" : "Place your bet";
            }

            string timer = string.Empty;
            if (board.TurnExpiresAt.HasValue)
            {
                double remaining = (board.TurnExpiresAt.Value - System.DateTimeOffset.UtcNow).TotalSeconds;
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

        private static bool CanSplit(HandView hand)
            => hand != null && hand.Cards.Count == 2 && hand.Cards[0].FaceVal == hand.Cards[1].FaceVal;

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
