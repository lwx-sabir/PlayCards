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
    [Tooltip("Returns true if any SoundContainers in the SoundEvent is set to looping")]
    public class SonityGetContainsLoop : SonityActionBase {
        [Tooltip("SoundEvent contains loop")]
        [RequiredField]
        [ObjectType(typeof(FsmBool))]
        [UIHint(UIHint.Variable)]
        public FsmBool storeBoolIn;

        public override bool HideGameObjectReference() => true;

        protected override void DoSoundEventAction() {
            storeBoolIn.Value = m_SoundEvent.GetContainsLoop();
        }
    }
}
#endif