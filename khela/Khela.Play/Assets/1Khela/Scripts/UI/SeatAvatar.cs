using System;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One seat's avatar ELEMENT — the portrait plus its state frames — rather than a bare Image. Put this on the
    /// avatar root (e.g. <c>Player_Avatar</c>), with the masked portrait and the frame variants underneath it:
    ///
    ///   Player_Avatar        &lt;- this component
    ///     Bg_Mask / portrait Image
    ///     Frame               (default / idle)
    ///     Frame_Playing       (this seat is acting)
    ///     Frame_Winner        (this seat won the round)
    ///
    /// <see cref="SeatPlate"/> references this instead of an Image, so the whole element is one thing to assign and
    /// the frames can be driven without the plate knowing how the art is built.
    ///
    /// The state frames are exposed but NOT driven automatically — nothing calls <see cref="SetState"/> yet. Wire it
    /// when you want it; until then the element behaves exactly like the single image it replaces.
    /// </summary>
    public sealed class SeatAvatar : MonoBehaviour
    {
        /// <summary>Which frame is showing. Extend alongside the frame fields below.</summary>
        public enum State { Idle, Playing, Winner }

        [Header("Portrait")]
        [Tooltip("The avatar picture itself (the Image inside the mask). Auto-found in children if left empty.")]
        [SerializeField] private Image portrait;

        [Header("State frames — only one is shown at a time")]
        [Tooltip("Default frame, shown whenever the seat is neither acting nor the winner.")]
        [SerializeField] private GameObject frameIdle;
        [Tooltip("Shown while it is THIS seat's turn.")]
        [SerializeField] private GameObject framePlaying;
        [Tooltip("Shown when this seat won the round.")]
        [SerializeField] private GameObject frameWinner;

        [Header("Turn countdown")]
        [Tooltip("The radial Image ON the playing frame — Image Type = Filled, Fill Method = Radial 360. Drains from " +
                 "full to empty across that seat's turn. Optional: leave empty for a playing frame with no countdown.")]
        [SerializeField] private Image playingFill;

        private State _state = State.Idle;

        // Absolute server deadline + the configured turn length. Kept rather than a countdown float so the ring is
        // derived from the clock every frame — a dropped board push or a hitch can't leave it stuck mid-drain.
        private DateTimeOffset? _turnEndsAt;
        private float _turnSeconds;

        /// <summary>The portrait Image, for anything that needs to tint or swap it directly.</summary>
        public Image Portrait => portrait;

        /// <summary>Current frame state.</summary>
        public State Current => _state;

        private void Awake()
        {
            if (portrait == null) portrait = GetComponentInChildren<Image>(true);
            SetState(_state);   // make the authored frames match the state rather than however they were left
        }

        /// <summary>Swap the avatar picture (profile photo, chosen icon). Ignores a null sprite so a failed load
        /// leaves the authored placeholder rather than blanking the portrait.</summary>
        public void SetPortrait(Sprite sprite)
        {
            if (portrait != null && sprite != null) portrait.sprite = sprite;
        }

        /// <summary>Show the frame for this state and hide the others.</summary>
        public void SetState(State state)
        {
            _state = state;
            // The idle frame is the avatar's BASE border and always stays — the other two are OVERLAYS drawn on top
            // (the turn ring, the winner flourish). Swapping the base out for them left the portrait unframed for the
            // whole turn, and the ring is authored to sit over the border, not to replace it.
            Set(frameIdle, true);
            Set(framePlaying, state == State.Playing);
            Set(frameWinner, state == State.Winner);
            UpdateFill();   // a frame switched ON must not show the previous turn's leftover ring
        }

        /// <summary>
        /// Arm the countdown ring with this seat's turn deadline. Pass a null deadline when the seat isn't acting.
        ///
        /// Takes the ABSOLUTE deadline, not a duration, so the ring stays correct without anyone driving it per frame —
        /// board pushes are far too sparse to animate against, and re-deriving from the clock means a missed push or a
        /// frame hitch self-corrects instead of leaving the ring stranded.
        /// </summary>
        public void SetTurn(DateTimeOffset? endsAt, float turnSeconds)
        {
            _turnEndsAt = endsAt;
            _turnSeconds = turnSeconds;
            UpdateFill();
        }

        private void Update()
        {
            if (_state == State.Playing) UpdateFill();
        }

        private void UpdateFill()
        {
            if (playingFill == null) return;

            if (_state != State.Playing || !_turnEndsAt.HasValue || _turnSeconds <= 0f)
            {
                playingFill.fillAmount = 1f;   // parked full, so the next turn starts from a whole ring
                return;
            }

            // The server stamps a GENEROUS ceiling and collapses it once that client says it can act, so remaining can
            // legitimately exceed the configured turn length. Clamping holds the ring full until the real clock starts
            // rather than showing a ring that appears to gain time.
            double remaining = (_turnEndsAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
            playingFill.fillAmount = Mathf.Clamp01((float)(remaining / _turnSeconds));
        }

        private static void Set(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }
    }
}
