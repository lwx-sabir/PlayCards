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
    [Tooltip("Returns the owner Transform used by MusicPlay() etc")]
    public class SonityMusicGetTransform : SonityActionBase {
        [Tooltip("The Transform of the SoundEvent")]
        [ObjectType(typeof(Transform))]
        public FsmObject storeTransformIn;

        public override bool HideGameObjectReference() => true;

        protected override void DoSoundEventAction() {
            storeTransformIn.Value = m_SoundEvent.MusicGetTransform();
        }
    }
}
#endif