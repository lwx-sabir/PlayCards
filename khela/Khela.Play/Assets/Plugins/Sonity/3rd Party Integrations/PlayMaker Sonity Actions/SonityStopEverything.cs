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
    [Tooltip("Stops all SoundEvents")]
    public class SonityStopEverything : SonitySoundManagerActionBase {

        [Tooltip("Enable fadeout for sound when stopping playback")]
        public FsmBool allowFadeout = true;

        protected override void DoSoundManagerAction() {
            m_SoundManager.StopEverything(allowFadeout.Value);
        }

        public override bool HideGameObjectReference() => true;
    }
}
#endif