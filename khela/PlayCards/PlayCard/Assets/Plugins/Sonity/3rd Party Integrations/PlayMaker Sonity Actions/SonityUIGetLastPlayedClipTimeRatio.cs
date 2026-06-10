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
    [Tooltip("Returns the length (in seconds) of the AudioClip in the last played AudioSource")]
    public class SonityUIGetLastPlayedClipTimeRatio : SonityActionBase {
        [Tooltip("Length of clip in seconds.")]
        [RequiredField]
        [ObjectType(typeof(FsmFloat))]
        [UIHint(UIHint.Variable)]
        public FsmFloat storeRatioIn;

        protected override void DoSoundEventAction() {
            storeRatioIn.Value = m_SoundEvent.UIGetLastPlayedClipTimeRatio();
        }

        public override bool HideGameObjectReference() => true;
    }
}
#endif