using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Pass
{
    /// <summary>
    /// Writes a 0..1 progress value to whatever the artist actually built the bar out of.
    ///
    /// This exists because of one trap that has already cost this project a debugging session, and would cost it again
    /// on every new screen that shows pass progress:
    ///
    /// <b>A Slider OWNS its fill Image.</b> <c>Slider.UpdateVisuals()</c> rewrites the fill's <c>fillAmount</c> (and its
    /// anchors) from the Slider's own <c>value</c> whenever that value, the rect, or the layout changes. So code that
    /// sets <c>fillAmount</c> directly on an Image that happens to live inside a Slider is silently undone, usually in
    /// the same frame — the bar just never moves, with nothing in the console. And which of the two an artist wires
    /// into an inspector field is a coin toss: the Image is the thing they can see filling, so it is the one they drag.
    ///
    /// So the rule is: FIND the Slider, even when only the Image was assigned, and drive the Slider. Only when there is
    /// genuinely no Slider is the Image written directly.
    ///
    /// Resolved ONCE per instance. Consumers keep their own serialized <c>Slider</c>/<c>Image</c> fields — deliberately,
    /// so wiring already done in a prefab keeps working — and hand them here.
    /// </summary>
    public sealed class PassProgressBar
    {
        private readonly Slider _slider;
        private readonly Image _fill;

        /// <summary>Resolve the bar from whatever was assigned. Either may be null; both null is valid and inert.</summary>
        public PassProgressBar(Slider slider, Image fill)
        {
            _fill = fill;

            // The assigned Slider wins; failing that, the Slider that owns the assigned fill Image. `true` includes
            // inactive parents, because a HUD is routinely wired while its panel is switched off.
            _slider = slider != null ? slider
                    : (fill != null ? fill.GetComponentInParent<Slider>(true) : null);

            if (_slider == null) return;

            // A progress bar is a READOUT. Left interactable, a drag across it moves the value — so the bar would show
            // a day the player isn't on — and it eats drags meant for the ScrollRect underneath it.
            _slider.interactable = false;
            if (_slider.targetGraphic != null) _slider.targetGraphic.raycastTarget = false;
            _slider.minValue = 0f;
            _slider.maxValue = 1f;
        }

        /// <summary>True when there is something to write to at all.</summary>
        public bool Exists => _slider != null || _fill != null;

        /// <summary>The bar's own transform — for anything that needs to measure or resize it.</summary>
        public RectTransform Rect => _slider != null ? _slider.transform as RectTransform
                                   : (_fill != null ? _fill.rectTransform : null);

        /// <summary>Show <paramref name="progress01"/>, clamped. Silently does nothing if nothing was assigned.</summary>
        public void Set(float progress01)
        {
            float p = Mathf.Clamp01(progress01);

            // SetValueWithoutNotify: a progress write is not a user edit, and any onValueChanged listener an artist
            // wired up should not fire because the month advanced.
            if (_slider != null) { _slider.SetValueWithoutNotify(p); return; }
            if (_fill != null) _fill.fillAmount = p;
        }
    }
}
