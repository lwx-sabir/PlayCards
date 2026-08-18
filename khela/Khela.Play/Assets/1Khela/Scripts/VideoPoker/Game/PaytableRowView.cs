using PlayCard.VideoPoker.Dtos;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.VideoPoker.Game
{
    /// <summary>One paytable row: a hand name + its payout per coin count. Wire up to five <see cref="coinCells"/>
    /// (coins 1..5) — or just one to show only the value at the current bet. The max-bet column uses the row's
    /// <c>AtMaxCoins</c>, so the royal-flush jackpot shows correctly.</summary>
    public sealed class PaytableRowView : MonoBehaviour
    {
        [SerializeField] private TMP_Text handLabel;
        [Tooltip("Coin columns 1..N, left → right. One cell = show only the active-bet value.")]
        [SerializeField] private TMP_Text[] coinCells;
        [Tooltip("Optional row background, tinted when this row is the winning hand.")]
        [SerializeField] private Image rowBackground;
        [SerializeField] private Color activeColumnColor = new Color(0.2f, 0.5f, 1f);
        [SerializeField] private Color winRowColor = new Color(0.25f, 0.8f, 0.4f, 0.5f);

        private Color _cellBase = Color.white;
        private Color _rowBase;

        public string Hand { get; private set; }

        public void Set(VpPaytableRow row, int maxCoins, int activeCoins)
        {
            Hand = row.Hand;
            if (handLabel) handLabel.text = row.Hand;
            if (coinCells != null && coinCells.Length > 0) _cellBase = coinCells[0] ? coinCells[0].color : Color.white;
            if (rowBackground) _rowBase = rowBackground.color;

            if (coinCells == null) return;
            bool single = coinCells.Length == 1;
            for (int c = 0; c < coinCells.Length; c++)
            {
                if (coinCells[c] == null) continue;
                int coinN = single ? activeCoins : c + 1;
                int val = coinN >= maxCoins ? row.AtMaxCoins : row.PerCoin * coinN;
                coinCells[c].text = val > 0 ? val.ToString() : "-";
                coinCells[c].color = (!single && coinN == activeCoins) ? activeColumnColor : _cellBase;
            }
        }

        public void SetWinning(bool winning)
        {
            if (rowBackground) rowBackground.color = winning ? winRowColor : _rowBase;
        }
    }
}
