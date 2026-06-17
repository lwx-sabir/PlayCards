using System;
using Khela.Common.Blackjack;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One row in the lobby table browser — binds a <see cref="BlackjackTableSummary"/> and reports a
    /// join request back to the lobby. All fields optional so the row prefab can be wired incrementally.
    /// </summary>
    public sealed class TableRowView : MonoBehaviour
    {
        [SerializeField] private TMP_Text stakeText;
        [SerializeField] private TMP_Text playersText;
        [SerializeField] private TMP_Text modeText;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private Button joinButton;

        private string _tableId;
        private Action<string> _onJoin;

        public void Bind(BlackjackTableSummary summary, Action<string> onJoin)
        {
            _tableId = summary.TableId;
            _onJoin = onJoin;

            if (stakeText != null) stakeText.text = $"{summary.MinBet:0} – {summary.MaxBet:0}";
            if (playersText != null) playersText.text = $"{summary.SeatsOccupied}/{summary.MaxPlayers}";
            if (modeText != null) modeText.text = summary.Mode.ToString();
            if (statusText != null) statusText.text = summary.RoundInProgress ? "In play" : "Open";

            if (joinButton != null)
            {
                joinButton.onClick.RemoveAllListeners();
                joinButton.onClick.AddListener(() => _onJoin?.Invoke(_tableId));
                joinButton.interactable = summary.SeatsOccupied < summary.MaxPlayers;
            }
        }
    }
}
