using System;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One pending reward in the inbox (level-up, gift, achievement…). Pure VIEW — bound by
    /// <see cref="RewardInboxBinder"/>; the Collect button raises a callback with the reward id.
    /// </summary>
    public sealed class RewardItemBinder : MonoBehaviour
    {
        [SerializeField] private Image iconImage;
        [Tooltip("Icon per reward Source (0=LevelUp,1=Milestone,2=DailyBonus,3=Pass,4=Achievement,5=Gift,6=Admin). Optional.")]
        [SerializeField] private Sprite[] sourceIcons;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text amountText;
        [SerializeField] private Button collectButton;
        [SerializeField] private string moneyFormat = "#,0";

        private string _id;
        private Action<string> _onCollect;

        public void Setup(RewardData r, Action<string> onCollect)
        {
            _id = r.Id;
            _onCollect = onCollect;

            SetText(titleText, r.Title);
            SetText(amountText, r.Amount.ToString(moneyFormat));
            if (iconImage != null && sourceIcons != null && r.Source >= 0 && r.Source < sourceIcons.Length)
            {
                var s = sourceIcons[r.Source];
                iconImage.sprite = s;
                iconImage.enabled = s != null;
            }
            if (collectButton != null)
            {
                collectButton.interactable = true;
                collectButton.onClick.RemoveAllListeners();
                collectButton.onClick.AddListener(() => _onCollect?.Invoke(_id));
            }
        }

        public void SetInteractable(bool on) { if (collectButton != null) collectButton.interactable = on; }

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
    }
}
