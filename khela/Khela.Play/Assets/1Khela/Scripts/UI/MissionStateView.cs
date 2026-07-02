using System;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// ONE state's view of a mission row (Progressing / Claimable / Claimed). Each is a self-contained layout with its
    /// OWN fields — put this on each state child of the row and assign only the fields THAT state actually has
    /// (Claimed has no slider/Claim button, Claimable has no slider, etc. — all null-guarded). <see cref="MissionEntryBinder"/>
    /// activates exactly one and calls <see cref="Bind"/> on it.
    /// </summary>
    public sealed class MissionStateView : MonoBehaviour
    {
        [SerializeField] private Image iconImage;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text descText;
        [SerializeField] private Slider progressSlider;     // Progressing only
        [SerializeField] private TMP_Text progressText;     // "2/5" — Progressing only
        [SerializeField] private TMP_Text rewardText;       // the reward amount
        [SerializeField] private Button claimButton;        // Claimable only
        [SerializeField] private string moneyFormat = "#,0";

        private string _id;
        private Action<string> _onClaim;

        public void Bind(MissionData m, Sprite icon, Action<string> onClaim)
        {
            _id = m.Id;
            _onClaim = onClaim;

            if (iconImage != null) { iconImage.sprite = icon; iconImage.enabled = icon != null; }
            SetText(titleText, m.Title);
            SetText(descText, m.Description);
            SetText(progressText, m.IsComplete ? "Complete" : $"{m.Progress.ToString(moneyFormat)}/{m.Target.ToString(moneyFormat)}");
            if (progressSlider != null)
            {
                progressSlider.minValue = 0f;
                progressSlider.maxValue = m.Target > 0 ? m.Target : 1f;
                progressSlider.value = m.Progress;
            }
            SetText(rewardText, m.RewardAmount.ToString(moneyFormat));

            if (claimButton != null)
            {
                claimButton.interactable = true;
                claimButton.onClick.RemoveAllListeners();
                claimButton.onClick.AddListener(() => _onClaim?.Invoke(_id));
            }
        }

        /// <summary>Bind this (Claimable) view from a raw pending REWARD — title + description + amount + Claim. A reward
        /// has no progress, so the slider is hidden. Lets level-up/gift rewards render as rows in the same missions list.</summary>
        public void BindReward(string id, string title, string description, decimal amount, Sprite icon, Action<string> onClaim)
        {
            _id = id;
            _onClaim = onClaim;

            if (iconImage != null) { iconImage.sprite = icon; iconImage.enabled = icon != null; }
            SetText(titleText, title);
            SetText(descText, description);
            SetText(progressText, "");
            if (progressSlider != null) progressSlider.gameObject.SetActive(false);
            SetText(rewardText, amount.ToString(moneyFormat));

            if (claimButton != null)
            {
                claimButton.interactable = true;
                claimButton.onClick.RemoveAllListeners();
                claimButton.onClick.AddListener(() => _onClaim?.Invoke(_id));
            }
        }

        public void SetClaimInteractable(bool on) { if (claimButton != null) claimButton.interactable = on; }

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
    }
}
