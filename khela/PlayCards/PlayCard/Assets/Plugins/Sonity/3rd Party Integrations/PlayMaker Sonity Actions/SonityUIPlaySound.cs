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
    [Tooltip("Play a UI Sound Event")]
    public class SonityUIPlaySound : SonityPlaySound {

        public override bool HideGameObjectReference() => true;

        public override void DoSoundParameterUpdate() {
            if (SoundManager.Instance.UIGetSoundEventState(m_SoundEvent) == SoundEventState.NotPlaying) {
                return;
            }
            UpdateParameterValues();
        }

        protected override IPlayMethod SetIPlayMethod() => new IPlayUISound();

        protected override SoundEventArgumentBuilder CreateSoundEventDefault() =>
            base.CreateSoundEventDefault()
            .WithSoundTag(m_SoundTag)
            .Build();
    }
}
#endif
