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
    [Tooltip("Returns the last played AudioSource")]
    public class SonityGetLastPlayedAudioSource : SonityActionBase {
        [Tooltip("Variable to store returned AudioSource in")]
        [ObjectType(typeof(AudioSource))]
        [UIHint(UIHint.Variable)]
        public FsmObject storeResultIn;

        protected override void DoSoundEventAction() {
            storeResultIn = m_SoundEvent.GetLastPlayedAudioSource(m_Transform);
        }
    }
}
#endif