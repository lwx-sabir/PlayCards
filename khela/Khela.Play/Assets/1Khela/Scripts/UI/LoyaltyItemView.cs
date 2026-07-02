using System;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One Loyalty-store item row/card. Pure VIEW — bound by <see cref="LoyaltyStoreBinder"/> via <see cref="Setup"/>;
    /// the Buy button raises a callback with the item id (the binder owns the redeem call). Assign only the fields your
    /// prefab actually has — every binding is null-guarded, so unassigned slots are skipped.
    ///
    /// Buy is enabled only when the item is both AFFORDABLE (balance ≥ cost) and UNLOCKED (VIP tier meets the gate);
    /// a locked item shows <see cref="lockedOverlay"/>. The server is authoritative — this view only reflects the
    /// flags the store endpoint already computed.
    /// </summary>
    public sealed class LoyaltyItemView : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameText;
        [Tooltip("LP price, e.g. \"250 LP\".")]
        [SerializeField] private TMP_Text costText;
        [Tooltip("Reward, e.g. \"5,000 Chips\". Blank for non-chip items.")]
        [SerializeField] private TMP_Text rewardText;
        [Tooltip("Optional item icon (chips stack / VIP crest).")]
        [SerializeField] private Image iconImage;
        [SerializeField] private Button buyButton;
        [Tooltip("Shown when the item is VIP-locked (tier too low).")]
        [SerializeField] private GameObject lockedOverlay;

        [Header("Formatting")]
        [SerializeField] private string moneyFormat = "#,0";

        private string _itemId;
        private Action<string> _onBuy;

        /// <summary>Bind an item + a buy callback. Call once per spawn.</summary>
        public void Setup(LoyaltyStoreItemData item, Action<string> onBuy)
        {
            _itemId = item.Id;
            _onBuy = onBuy;

            SetText(nameText, item.Name);
            SetText(costText, item.CostLp.ToString(moneyFormat) + " LP");
            SetText(rewardText, item.ChipAmount > 0m ? item.ChipAmount.ToString(moneyFormat) + " Chips" : "");
            SetActiveSafe(lockedOverlay, !item.Unlocked);

            if (buyButton != null)
            {
                buyButton.interactable = item.Affordable && item.Unlocked;
                buyButton.onClick.RemoveAllListeners();
                buyButton.onClick.AddListener(() => _onBuy?.Invoke(_itemId));
            }
        }

        /// <summary>Lock/unlock the buy button during an in-flight redeem (prevents double-tap).</summary>
        public void SetBuyInteractable(bool on) { if (buyButton != null) buyButton.interactable = on; }

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
        private static void SetActiveSafe(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }
    }
}
