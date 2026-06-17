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
    [Tooltip("Returns the owner Transform used by UIPlay() etc")]
    public class SonityUIGetTransform : SonityActionBase {
        [Tooltip("The Transform of the SoundEvent")]
        [ObjectType(typeof(Transform))]
        [RequiredField]
        [UIHint(UIHint.Variable)]
        public FsmObject storeTransformIn;

        public override bool HideGameObjectReference() => true;

        protected override void DoSoundEventAction() {
            storeTransformIn.Value = m_SoundEvent.UIGetTransform();
        }
    }
}
#endif