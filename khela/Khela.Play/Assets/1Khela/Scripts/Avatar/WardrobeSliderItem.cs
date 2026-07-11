using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    /// <summary>
    /// One customization slider row — a label + a 0..1 slider. <see cref="WardrobeShapeSliders"/> spawns one per shape,
    /// binds it, and applies changes live. Pure view: it fills the label, sets the handle without firing, and reports
    /// drags. Prefab: drag in the label text + the Slider.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeSliderItem : MonoBehaviour
    {
        [Tooltip("The shape's name label.")]
        [SerializeField] private TMP_Text label;
        [Tooltip("The 0..1 slider. Min/Max are forced to 0/1 on bind.")]
        [SerializeField] private Slider slider;

        private Action<float> _onChanged;

        private void Awake()
        {
            if (slider != null) slider.onValueChanged.AddListener(v => _onChanged?.Invoke(v));
        }

        /// <summary>Fill the row and hook its drag. The initial value is set WITHOUT firing, so binding never nudges the shape.</summary>
        public void Bind(string labelText, float value01, Action<float> onChanged)
        {
            _onChanged = null;                       // suppress the callback while we set the starting value
            if (label != null) label.text = labelText;
            if (slider != null)
            {
                slider.minValue = 0f;
                slider.maxValue = 1f;
                slider.SetValueWithoutNotify(Mathf.Clamp01(value01));
            }
            _onChanged = onChanged;
        }
    }
}
