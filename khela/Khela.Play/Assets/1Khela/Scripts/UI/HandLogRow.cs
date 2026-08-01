using System;
using PlayCard.Game.Dtos;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// ONE row of the session hand log (see <see cref="HandLogView"/>) — one settled hand: when it was played, what
    /// was staked, the final total, the outcome, and the net. A split round produces TWO rows sharing a round number,
    /// tagged "Hand 1" / "Hand 2".
    ///
    /// Put this on your row prefab and assign whichever labels your design actually has — EVERY field is optional, so
    /// a minimal row (say, just outcome + delta) works without touching this script. Nothing here decides colours or
    /// layout beyond the three outcome tints; the look is entirely your prefab.
    /// </summary>
    public sealed class HandLogRow : MonoBehaviour
    {
        [Header("Labels (all optional — assign only what your row shows)")]
        [Tooltip("Round number, e.g. \"#42\". A split's two rows share it.")]
        [SerializeField] private TMP_Text roundLabel;
        [Tooltip("Local time the hand settled, formatted by Time Format.")]
        [SerializeField] private TMP_Text timeLabel;
        [Tooltip("Total staked on this hand (bet + insurance).")]
        [SerializeField] private TMP_Text betLabel;
        [Tooltip("The hand's final total, e.g. \"20\" — or the busted total, e.g. \"23\".")]
        [SerializeField] private TMP_Text handValueLabel;
        [Tooltip("WIN / LOSE / PUSH / BLACKJACK / BUST.")]
        [SerializeField] private TMP_Text outcomeLabel;
        [Tooltip("Signed net for this hand, e.g. \"+1,500\" / \"-500\".")]
        [SerializeField] private TMP_Text deltaLabel;
        [Tooltip("Only shown on a SPLIT round's rows, e.g. \"Hand 2\". Hidden (SetActive false) otherwise.")]
        [SerializeField] private GameObject splitTag;
        [Tooltip("Label inside Split Tag, if it has one.")]
        [SerializeField] private TMP_Text splitTagLabel;

        [Header("Verify (optional — provably-fair)")]
        [Tooltip("Copies this hand's id to the clipboard so the player can check it against " +
                 "/api/Blackjack/verify/{handId}. Hidden if unassigned.")]
        [SerializeField] private Button verifyButton;

        [Header("Formatting")]
        [Tooltip("Number format for chip amounts.")]
        [SerializeField] private string amountFormat = "#,0";
        [Tooltip("Time format for the settled-at stamp (local time).")]
        [SerializeField] private string timeFormat = "HH:mm";

        [Header("Outcome tint (applied to Outcome + Delta labels)")]
        [SerializeField] private Color winColor = new Color(0.30f, 0.85f, 0.40f);
        [SerializeField] private Color loseColor = new Color(0.90f, 0.35f, 0.35f);
        [SerializeField] private Color pushColor = new Color(0.85f, 0.80f, 0.45f);

        private string _handId;

        private void Awake()
        {
            if (verifyButton != null) verifyButton.onClick.AddListener(CopyHandId);
        }

        private void OnDestroy()
        {
            if (verifyButton != null) verifyButton.onClick.RemoveListener(CopyHandId);
        }

        /// <summary>Fill this row from one settled hand. Called by <see cref="HandLogView"/>.</summary>
        public void Bind(HandLogEntry e)
        {
            if (e == null) return;
            _handId = e.HandId;

            bool win = e.Delta > 0m;
            bool lose = e.Delta < 0m;
            Color tint = win ? winColor : lose ? loseColor : pushColor;

            // SessionRound counts THIS sitting (1, 2, 3 …); HandNumber is the table's all-time counter and is only the
            // fallback if the view didn't renumber (it always does).
            if (roundLabel != null) roundLabel.text = "#" + (e.SessionRound > 0 ? e.SessionRound : e.HandNumber);
            if (timeLabel != null)
                timeLabel.text = e.SettledAt.HasValue ? e.SettledAt.Value.ToLocalTime().ToString(timeFormat) : "";
            if (betLabel != null) betLabel.text = (e.Bet + e.InsuranceBet).ToString(amountFormat);
            // Always the real total, including a bust (22, 23 …) — the OUTCOME column already says "BUST", so printing
            // it here too said the same thing twice and hid the one number this column exists to show.
            if (handValueLabel != null) handValueLabel.text = e.FinalHandValue.ToString();

            if (outcomeLabel != null)
            {
                outcomeLabel.text = OutcomeText(e);
                outcomeLabel.color = tint;
            }
            if (deltaLabel != null)
            {
                // Always signed, so a row reads as a gain or a loss at a glance. Push shows a plain 0. The sign is
                // prepended explicitly (not left to the format string, which never emits "+" and can be configured to
                // drop "-").
                decimal mag = e.Delta < 0m ? -e.Delta : e.Delta;
                string sign = e.Delta > 0m ? "+" : e.Delta < 0m ? "-" : "";
                deltaLabel.text = sign + mag.ToString(amountFormat);
                deltaLabel.color = tint;
            }

            // The split tag only makes sense when the round actually split — HandLogView works that out by looking
            // at sibling entries, since one entry alone can't tell.
            if (splitTag != null) splitTag.SetActive(e.IsSplitPart);
            if (splitTagLabel != null && e.IsSplitPart) splitTagLabel.text = "Hand " + (e.HandIndex + 1);

            if (verifyButton != null) verifyButton.gameObject.SetActive(!string.IsNullOrEmpty(_handId));
        }

        private static string OutcomeText(HandLogEntry e)
        {
            if (e.Bust) return "BUST";
            switch (e.Outcome)
            {
                case "blackjack": return "BLACKJACK";
                case "win": return "WIN";
                case "push": return "PUSH";
                case "bust": return "BUST";
                default: return "LOSE";
            }
        }

        // Provably-fair: hand the player the id so they can verify the shuffle themselves.
        private void CopyHandId()
        {
            if (!string.IsNullOrEmpty(_handId)) GUIUtility.systemCopyBuffer = _handId;
        }
    }
}
