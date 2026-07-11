using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    /// <summary>
    /// One tab VIEW — just a button with an icon + name. You build TWO prefabs from your two mockup states (the
    /// selected look and the default look) and put this on BOTH; <see cref="WardrobeTabBar"/> spawns one of each per
    /// category and swaps which is shown. So this component holds NO selected/unselected state — the prefab IS the
    /// state. It only fills its icon/label and reports clicks.
    ///
    /// Prefab wiring: drag in the Button (auto-fills if it's on the root), the icon Image, and the name text.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeTab : MonoBehaviour
    {
        [Tooltip("The clickable button. Defaults to a Button on this object.")]
        [SerializeField] private Button button;
        [Tooltip("The category icon (the BoZo/slot picture).")]
        [SerializeField] private Image icon;
        [Tooltip("The category name label.")]
        [SerializeField] private TMP_Text label;

        /// <summary>The category key this tab represents (a BoZo slot name, e.g. "Top"). Set on <see cref="Bind"/>.</summary>
        public string Key { get; private set; }

        private Action<WardrobeTab> _onClick;

        private void Reset() => button = GetComponent<Button>();

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            if (button != null) button.onClick.AddListener(() => _onClick?.Invoke(this));
        }

        /// <summary>Fill the tab with its category and hook its click. Called once by the tab bar on spawn.</summary>
        public void Bind(string key, string labelText, Sprite iconSprite, Action<WardrobeTab> onClick)
        {
            Key = key;
            _onClick = onClick;
            if (label != null) label.text = labelText;
            if (icon != null)
            {
                icon.sprite = iconSprite;
                icon.enabled = iconSprite != null;
            }
        }
    }
}
