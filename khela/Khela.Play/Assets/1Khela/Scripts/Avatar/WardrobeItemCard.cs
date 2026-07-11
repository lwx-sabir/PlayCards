using System;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    /// <summary>
    /// One item card in the wardrobe grid — the item's baked image, its price, and its colour-option count. Pure view:
    /// <see cref="WardrobeItemGrid"/> binds the data + loads the image, and this reports the click. Build the prefab
    /// with a Button, an Image for the picture, a price text, a colours-count text, and (optional) an "owned" marker
    /// shown when the player already has it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeItemCard : MonoBehaviour
    {
        [Tooltip("The clickable button. Defaults to a Button on this object.")]
        [SerializeField] private Button button;
        [Tooltip("The item's baked product image.")]
        [SerializeField] private Image image;
        [Tooltip("Price text (shows 'Free' for starters/0).")]
        [SerializeField] private TMP_Text priceText;
        [Tooltip("Colour-options count (palette swatches, or 1).")]
        [SerializeField] private TMP_Text colorsText;
        [Tooltip("Optional: shown when the player already owns this item.")]
        [SerializeField] private GameObject ownedMarker;
        [Tooltip("Optional: shown when this item is the currently-equipped one in its slot (the selected frame).")]
        [SerializeField] private GameObject selectedState;

        public CosmeticItemDto Item { get; private set; }

        private Action<WardrobeItemCard> _onClick;

        private void Reset() => button = GetComponent<Button>();

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            if (button != null) button.onClick.AddListener(() => _onClick?.Invoke(this));
        }

        /// <summary>Fill the card. Icon can be null now and set later via <see cref="SetImage"/> once it downloads.</summary>
        public void Bind(CosmeticItemDto item, Action<WardrobeItemCard> onClick, Sprite icon = null)
        {
            Item = item;
            _onClick = onClick;
            SetImage(icon);
            if (priceText != null) priceText.text = item.IsFree ? "Free" : item.Price.ToString("0");
            if (colorsText != null) colorsText.text = item.ColorCount.ToString();
            if (ownedMarker != null) ownedMarker.SetActive(item.Owned);
            SetSelected(false);
        }

        /// <summary>Toggle the equipped/selected frame.</summary>
        public void SetSelected(bool on)
        {
            if (selectedState != null) selectedState.SetActive(on);
        }

        public void SetImage(Sprite icon)
        {
            if (image == null) return;
            image.sprite = icon;
            image.enabled = icon != null;
        }
    }
}
