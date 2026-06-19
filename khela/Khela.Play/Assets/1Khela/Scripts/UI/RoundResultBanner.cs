using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Shows the local player's round result after settle — WIN / LOSE / PUSH / BLACKJACK / BUST plus the net
    /// chip delta — read from the board's <see cref="BoardSnapshot.LastResults"/>. Hidden during play and
    /// until the first settle. Display only: the authoritative outcome + money already happened server-side.
    ///
    /// Visibility is driven by a <see cref="CanvasGroup"/> (alpha), NOT <c>GameObject.SetActive</c>. That lets
    /// the banner sit on the very GameObject it shows/hides: SetActive(false) on its own object would fire
    /// OnDisable and unsubscribe it forever, so it could never show again.
    /// </summary>
    public sealed class RoundResultBanner : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("The banner panel to show/hide. May be this same GameObject — a CanvasGroup is used so it " +
                 "won't disable itself.")]
        [SerializeField] private GameObject panel;
        [SerializeField] private TMP_Text label;

        [Header("Colors (optional)")]
        [SerializeField] private Color winColor = new Color(0.30f, 0.85f, 0.40f);
        [SerializeField] private Color loseColor = new Color(0.90f, 0.35f, 0.35f);
        [SerializeField] private Color pushColor = new Color(0.85f, 0.80f, 0.45f);

        private CanvasGroup group;

        private void Awake()
        {
            // Get/add a CanvasGroup on the panel so we can fade visibility without deactivating the object.
            if (panel != null)
            {
                group = panel.GetComponent<CanvasGroup>();
                if (group == null) group = panel.AddComponent<CanvasGroup>();
            }
        }

        private void OnEnable()
        {
            if (table != null) table.OnBoardChanged += OnBoard;
            Hide();
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            // Only after a round has settled (not in progress) and we have our seat's result.
            if (board == null || board.RoundInProgress || table.MySeat <= 0) { Hide(); return; }

            SeatResultView r = board.LastResults?.Find(x => x.SeatNumber == table.MySeat);
            if (r == null) { Hide(); return; }

            if (label != null)
            {
                label.text = Format(r);
                label.color = r.Bust || r.Outcome == "lose" ? loseColor
                            : r.Outcome == "push" ? pushColor
                            : winColor;
            }
            SetVisible(true);
        }

        private void Hide() => SetVisible(false);

        private void SetVisible(bool on)
        {
            if (group != null)
            {
                group.alpha = on ? 1f : 0f;
                group.blocksRaycasts = on;
                group.interactable = on;
            }
            else if (panel != null && panel != gameObject)
            {
                // No CanvasGroup (non-UI panel) and we're not sitting on it: plain activate is safe.
                panel.SetActive(on);
            }
        }

        private static string Format(SeatResultView r)
        {
            if (r.Bust) return "BUST";
            switch (r.Outcome)
            {
                case "win":  return (r.Blackjack ? "BLACKJACK!  " : "WIN  ") + $"+{r.Delta:#,0}";
                case "push": return "PUSH";
                default:     return $"LOSE  {r.Delta:#,0}";   // Delta is negative
            }
        }
    }
}
