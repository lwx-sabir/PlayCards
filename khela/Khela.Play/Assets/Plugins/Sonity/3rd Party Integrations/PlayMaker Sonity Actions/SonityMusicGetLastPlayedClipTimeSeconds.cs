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
    [Tooltip("Returns the time (in seconds) since the SoundEvent was played")]
    public class SonityMusicGetLastPlayedClipTimeSeconds : SonityActionBase {
        [Tooltip("Variable to store returned results in")]
        [UIHint(UIHint.Variable)]
        [ObjectType(typeof(FsmFloat))]
        public FsmFloat storeTimeIn;

        public override bool HideGameObjectReference() => true;

        [Tooltip(" pitchSpeed determines if it should be scaled by pitch.E.g. -12 semitones will be twice as long")]
        public FsmBool pitchSpeed;
        protected override void DoSoundEventAction() =>
            storeTimeIn.Value = m_SoundEvent.MusicGetLastPlayedClipLength(pitchSpeed.Value);
    }
}
#endif