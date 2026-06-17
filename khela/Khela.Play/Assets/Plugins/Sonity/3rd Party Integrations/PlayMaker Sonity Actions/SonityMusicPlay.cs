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
    [Tooltip("Plays the SoundEvent at the SoundManagers music Transform")]
    public class SonityMusicPlay : SonityPlaySound {
        [Tooltip("Stop the playback of all other music events")]
        public FsmBool stopAllOtherMusic = false;

        [Tooltip("Enable fadeout for sound when stopping playback")]
        public FsmBool allowFadeout = true;

        public override void DoSoundParameterUpdate() {
            if (SoundManager.Instance.MusicGetSoundEventState(m_SoundEvent) == SoundEventState.NotPlaying) {
                return;
            }

            UpdateParameterValues();
        }

        protected override IPlayMethod SetIPlayMethod() => new IPlayMusic();

        protected override PlayMethodDecorator PlayMethodFactory() =>
               new PlayMethodMusic(m_IPlayMethod, m_SoundEventArguments);

        protected override SoundEventArgumentBuilder CreateSoundEventDefault() =>
            base.CreateSoundEventDefault()
            .WithStopAllOtherMusic(stopAllOtherMusic.Value)
            .WithAllowFadeout(allowFadeout.Value)
            .Build();
    }
}
#endif