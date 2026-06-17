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
    [Tooltip("Stop a SoundEvent in Sonity")]
    public class SonityStopAtPosition : SonityActionBase {

        [Tooltip("Target GameObject to stop playback at.")]
        [RequiredField]
        public FsmGameObject targetGameObject;

        [Tooltip("Enable fadeout for sound when stopping playback")]
        public FsmBool allowFadeout = true;

        public override bool HideGameObjectReference() => true;

        protected override void DoSoundEventAction() {
            m_SoundEvent.StopAtPosition(targetGameObject.Value.transform, allowFadeout.Value);

        }
    }
}
#endif