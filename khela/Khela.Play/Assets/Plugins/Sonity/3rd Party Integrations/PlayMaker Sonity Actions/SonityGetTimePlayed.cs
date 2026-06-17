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
    public class SonityGetTimePlayed : SonityActionBase {
        [RequiredField]
        [ObjectType(typeof(FsmFloat))]
        [UIHint(UIHint.Variable)]
        [Tooltip("Time since Sound Event started playing")]
        public FsmFloat storeTimeIn;

        protected override void DoSoundEventAction() {
            storeTimeIn.Value = m_SoundEvent.GetTimePlayed(m_Transform);
        }
    }
}
#endif