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
    [Tooltip("Unpauses all SoundEvents at the Music Transform locally")]
    public class SonityMusicUnpauseAll : SonitySoundManagerActionBase {

        public override bool HideGameObjectReference() => true;

        protected override void DoSoundManagerAction() {
            m_SoundManager.MusicUnpauseAll();
        }
    }
}
#endif