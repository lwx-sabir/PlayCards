using System.Globalization;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The in-table action bar: bet entry + Deal/Hit/Stand/Double/Split/Insurance/DealerPlay/Leave,
    /// gated off the live board from <see cref="TableController"/>. Gating is UX only — the server
    /// re-validates every action; this just disables obviously-unavailable moves. Every field is
    /// optional (null-tolerant) so the Canvas can be wired up incrementally.
    /// </summary>
    public sealed class TableActionBar : MonoBehaviour
    {
        [Header("Controller")]
        [SerializeField] private TableController table;

        [Header("Betting")]
        [SerializeField] private TMP_InputField betInput;
        [SerializeField] private Button betButton;
        [SerializeField] private Button dealButton;

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
            Wire(betButton, PlaceBet);
            Wire(dealButton, () => _ = table.Deal());
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
            if (table == null) return;
            table.OnBoardChanged += Refresh;
            table.OnActionError += ShowError;
            Refresh(table.Board);
        }

        private void OnDisable()
        {
            if (table == null) return;
            table.OnBoardChanged -= Refresh;
            table.OnActionError -= ShowError;
        }

        private void PlaceBet()
        {
            var amount = ParseBet();
            if (amount <= 0) { ShowError("Enter a bet amount."); return; }
            ClearError();
            _ = table.PlaceBet(amount);
        }

        private void PlaceInsurance()
        {
            var hand = MyCurrentHand();
            if (hand != null) _ = table.Insurance(hand.Bet / 2m);
        }

        private void Refresh(BoardSnapshot board)
        {
            bool seated = table.MySeat > 0;
            bool inRound = board != null && board.RoundInProgress;
            bool myTurn = table.IsMyTurn;
            var hand = MyCurrentHand();

            if (betInput != null) betInput.interactable = !inRound && seated;
            Set(betButton, !inRound && seated);
            Set(dealButton, !inRound && seated);

            Set(hitButton, myTurn);
            Set(standButton, myTurn);
            Set(doubleButton, myTurn && hand != null && hand.Cards.Count == 2);
            Set(splitButton, myTurn && CanSplit(hand));
            Set(insuranceButton, myTurn && hand != null && hand.Insurance == 0 && DealerShowsAce(board));

            // No server round-driver yet: expose Dealer Play once everyone has acted (CurrentSeatNumber == -1).
            Set(dealerPlayButton, inRound && board != null && board.CurrentSeatNumber == -1);
            Set(leaveButton, true);

            if (statusText != null)
                statusText.text = !seated ? "Spectating" : inRound ? (myTurn ? "Your turn" : "Waiting…") : "Place your bet";
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

        private decimal ParseBet()
        {
            if (betInput == null) return 0;
            return decimal.TryParse(betInput.text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0;
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
