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
    [Tooltip("Pauses the SoundEvent everywhere locally")]
    public class SonityPauseEverywhere : SonityActionBase {

        [Tooltip("If the SoundEvent should be paused even if it is set to \"Ignore Local Pause\"")]
        public FsmBool forcePause = false;

        public override bool HideGameObjectReference() => true;

        protected override void DoSoundEventAction() {
            m_SoundEvent.PauseEverywhere(forcePause.Value);
        }
    }
}
#endif