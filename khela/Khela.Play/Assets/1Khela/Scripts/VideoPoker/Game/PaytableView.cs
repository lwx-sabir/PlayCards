using System.Collections.Generic;
using PlayCard.VideoPoker.Dtos;
using UnityEngine;

namespace PlayCard.VideoPoker.Game
{
    /// <summary>Builds the paytable for the selected variant (6 variants ship with different tables) by instantiating a
    /// <see cref="PaytableRowView"/> per row, and flashes the winning row on a result. Purely presentational — the
    /// authoritative payout always comes from the server board.</summary>
    public sealed class PaytableView : MonoBehaviour
    {
        [SerializeField] private Transform rowContainer;
        [SerializeField] private PaytableRowView rowPrefab;

        private readonly List<PaytableRowView> _rows = new List<PaytableRowView>();

        public void Show(VpVariantSummary variant, int activeCoins)
        {
            if (rowContainer == null || rowPrefab == null || variant == null) return;
            foreach (var r in _rows) if (r) Destroy(r.gameObject);
            _rows.Clear();

            foreach (var row in variant.Rows)
            {
                var v = Instantiate(rowPrefab, rowContainer);
                v.Set(row, variant.MaxCoins, activeCoins);
                _rows.Add(v);
            }
        }

        /// <summary>Flash the row matching the winning category (best-effort name match), or clear when null.</summary>
        public void HighlightWinning(string category)
        {
            string want = Normalize(category);
            foreach (var r in _rows)
                if (r) r.SetWinning(want != null && Matches(Normalize(r.Hand), want));
        }

        private static bool Matches(string rowHand, string category)
        {
            if (rowHand == category) return true;
            // "Pair"/"Jacks"/"Kings" all pay on the "jacks/kings or better" row.
            if (category.Contains("pair") && rowHand.Contains("orbetter")) return true;
            return false;
        }

        private static string Normalize(string s)
            => string.IsNullOrEmpty(s) ? null : s.Replace(" ", "").Replace("-", "").ToLowerInvariant();
    }
}
