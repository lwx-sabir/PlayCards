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
    public class SonityUIGetTimePlayed : SonityActionBase {
        [Tooltip("Time since Sound Event started playing")]
        [RequiredField]
        [ObjectType(typeof(FsmFloat))]
        [UIHint(UIHint.Variable)]
        public FsmFloat storeTimeIn;

        public override bool HideGameObjectReference() => true;

        protected override void DoSoundEventAction() {
            storeTimeIn.Value = m_SoundEvent.UIGetTimePlayed();
        }
    }
}
#endif