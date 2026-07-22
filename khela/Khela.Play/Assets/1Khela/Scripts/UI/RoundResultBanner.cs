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

        // Round-end HOLD: with a RoundEndDirector presenting the round-end, the WIN / LOSE / +delta banner is the loudest
        // "you got paid" signal — so it must wait for the director's PAY beat, NOT flash at the raw settle push. Held as a
        // MonoBehaviour so there's no hard type dependency on the director. Null = inert, shows at settle as before.
        private MonoBehaviour _settleDirector;
        private bool _revealed;

        /// <summary>The director arms deferral (called at its OnEnable): the banner waits for the PAY beat.</summary>
        public void RegisterSettleDirector(MonoBehaviour director) => _settleDirector = director;
        public void UnregisterSettleDirector(MonoBehaviour director) { if (_settleDirector == director) _settleDirector = null; }

        /// <summary>Director's PAY beat: show the result banner now.</summary>
        public void RevealNow(BoardSnapshot board)
        {
            _revealed = true;
            ShowResult(board ?? (table != null ? table.Board : null));
        }

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
            // In-round (or no seat) → hidden, and re-arm the deferral for the next settle.
            if (board == null || board.RoundInProgress || table.MySeat <= 0) { Hide(); _revealed = false; return; }

            // Settle push: with a director presenting, stay hidden until it calls RevealNow on the PAY beat. (It was
            // already hidden by the in-round push above, so we simply don't reveal yet.)
            if (_settleDirector != null && !_revealed) return;

            ShowResult(board);
        }

        private void ShowResult(BoardSnapshot board)
        {
            if (board == null || table == null || table.MySeat <= 0) { Hide(); return; }

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
