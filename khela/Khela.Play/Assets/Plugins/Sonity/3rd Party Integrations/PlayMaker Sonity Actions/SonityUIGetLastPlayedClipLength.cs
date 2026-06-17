// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER
using HutongGames.PlayMaker;
using UnityEngine;
using TooltipAttribute = HutongGames.PlayMaker.TooltipAttribute;
using Sonity.PlayMaker.Internal;

namespace Sonity.PlayMaker {

    [ActionCategory("Sonity")]
    [HelpURL("https://sonityaudio.github.io")]
    [Tooltip("Returns the current time (in seconds) of the AudioClip in the last played AudioSource")]
    public class SonityUIGetLastPlayedClipLength : SonityActionBase {

        [RequiredField]
        [ObjectType(typeof(FsmFloat))]
        [UIHint(UIHint.Variable)]
        [Tooltip("Length of clip in seconds.")]
        public FsmFloat storeLengthIn;

        [Tooltip(" pitchSpeed determines if it should be scaled by pitch.E.g. -12 semitones will be twice as long")]
        public FsmBool pitchSpeed;

        protected override void DoSoundEventAction() {
            storeLengthIn.Value = m_SoundEvent.UIGetLastPlayedClipLength(pitchSpeed.Value);
        }

        public override bool HideGameObjectReference() => true;
    }
}
#endif