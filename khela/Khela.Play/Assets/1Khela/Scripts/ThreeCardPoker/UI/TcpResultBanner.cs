using PlayCard.ThreeCardPoker.Dtos;
using PlayCard.ThreeCardPoker.Table;
using TMPro;
using UnityEngine;

namespace PlayCard.ThreeCardPoker.UI
{
    /// <summary>
    /// Shows this player's settle result after a 3CP round — the net chips won/lost across all circles. Reads the
    /// seat's <see cref="TcpSeatView.LastReturn"/> (gross returned) minus what it staked, so it correctly reflects a
    /// folded main hand whose Pair Plus / 6-Card still paid. Visibility toggles a <see cref="CanvasGroup"/> (NOT
    /// SetActive) so this watcher keeps its board subscription — put it on an always-active object.
    /// </summary>
    public sealed class TcpResultBanner : MonoBehaviour
    {
        [SerializeField] private TcpTableController controller;
        [Tooltip("The banner panel to show/hide (its CanvasGroup is used). Defaults to this GameObject.")]
        [SerializeField] private GameObject panel;
        [SerializeField] private TMP_Text label;

        [SerializeField] private Color winColor = new Color(0.29f, 0.87f, 0.50f);
        [SerializeField] private Color loseColor = new Color(0.97f, 0.44f, 0.44f);
        [SerializeField] private Color pushColor = new Color(0.84f, 0.73f, 0.30f);
        [SerializeField] private string amountFormat = "#,0";

        private CanvasGroup _group;

        private void Awake()
        {
            if (panel == null) panel = gameObject;
            _group = panel.GetComponent<CanvasGroup>() ?? panel.AddComponent<CanvasGroup>();
            Hide();
        }

        private void OnEnable()
        {
            if (controller == null) return;
            controller.OnBoardChanged += OnBoard;
            if (controller.Board != null) OnBoard(controller.Board);
        }

        private void OnDisable()
        {
            if (controller != null) controller.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(TcpBoard board)
        {
            // Show only on a settled round, for our seat, when we were actually in the round.
            if (board == null || board.Phase != "complete") { Hide(); return; }
            var seat = controller.MySeatView;
            if (seat == null || !seat.InRound) { Hide(); return; }

            decimal net = seat.LastReturn - seat.TotalStaked;
            if (label != null)
            {
                if (net > 0m) { label.text = $"WIN  +{net.ToString(amountFormat)}"; label.color = winColor; }
                else if (net < 0m) { label.text = $"LOSE  {net.ToString(amountFormat)}"; label.color = loseColor; }
                else { label.text = "EVEN"; label.color = pushColor; }
            }
            Show();
        }

        private void Show() { _group.alpha = 1f; _group.interactable = true; _group.blocksRaycasts = true; }
        private void Hide() { _group.alpha = 0f; _group.interactable = false; _group.blocksRaycasts = false; }
    }
}
