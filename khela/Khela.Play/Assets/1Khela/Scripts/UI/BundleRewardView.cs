using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>One reward tile in the daily-mission bundle — a currency icon + amount. Bound by
    /// <see cref="DailyBundleBinder"/> from the server's bundle list.</summary>
    public sealed class BundleRewardView : MonoBehaviour
    {
        [SerializeField] private Image iconImage;
        [SerializeField] private TMP_Text amountText;
        [SerializeField] private string moneyFormat = "#,0";

        public void Setup(Sprite icon, decimal amount)
        {
            if (iconImage != null) { iconImage.sprite = icon; iconImage.enabled = icon != null; }
            if (amountText != null) amountText.text = amount.ToString(moneyFormat);
        }
    }
}
