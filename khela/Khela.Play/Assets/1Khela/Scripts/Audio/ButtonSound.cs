using Sonity;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PlayCard.Audio
{
    /// <summary>
    /// Click sound for one Button. Deliberately a component per button rather than a global runtime scan: the sound a
    /// button makes is part of how it is authored, so it belongs in the Inspector where you can see and override it.
    /// Add it in bulk with <c>Khela ▸ Audio ▸ Add Button Sound To Selection</c>.
    ///
    /// Hooks <see cref="IPointerClickHandler"/> rather than <c>Button.onClick</c>, so it also fires on a DISABLED
    /// button — that is the whole point of the Denied sound. onClick never fires when a button is non-interactable, so
    /// a button wired that way is silent exactly when the player most needs to be told no.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    public sealed class ButtonSound : MonoBehaviour, IPointerClickHandler
    {
        [Tooltip("Played on a normal click. Leave empty for a silent button.")]
        [SerializeField] private SoundEvent click;

        [Tooltip("Played instead when the button is NOT interactable — the 'you can't do that' tick. Optional; leave " +
                 "empty and a blocked press is silent.")]
        [SerializeField] private SoundEvent denied;

        private Button _button;

        private void Awake() => _button = GetComponent<Button>();

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_button == null) _button = GetComponent<Button>();
            bool usable = _button == null || _button.interactable;

            var sound = usable ? click : denied;
            // UIPlay, not Play2D: Play2D is the LEGACY name (behind SONITY_ENABLE_LEGACY_FUNCTIONS_MUSIC_AND_2D, which
            // is not defined here) — UIPlay is the same thing under its current name. It owns the sound on the
            // SoundManager's own UI transform, which is what makes SoundEvent.UIStopAll() able to kill every UI sound
            // on a panel teardown.
            //
            // It does NOT make the sound 2D. A sound is 2D because its SoundContainer says so — Spatial Blend 0 and
            // Enable Distance off. Author the UI containers that way or a button will pan with the camera.
            if (sound != null) sound.UIPlay();
        }

        /// <summary>Assign the sounds from tooling (the bulk editor menu) without exposing the fields publicly.</summary>
        public void Configure(SoundEvent clickSound, SoundEvent deniedSound)
        {
            click = clickSound;
            denied = deniedSound;
        }
    }
}
