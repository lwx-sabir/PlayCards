// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER
using HutongGames.PlayMaker;
using Sonity.Internal;
using UnityEngine;
using TooltipAttribute = HutongGames.PlayMaker.TooltipAttribute;
using Sonity.PlayMaker.Internal;

namespace Sonity.PlayMaker {

    [ActionCategory("Sonity")]
    [HelpURL("https://sonityaudio.github.io")]
    [Tooltip("Play Sound")]
    public class SonityPlaySound : SonityPlayActionBase {
        protected SoundTag m_SoundTag;

        [Tooltip("Not required. Used to set the soundtag of this event.")]
        [ObjectType(typeof(SoundTagBase))]
        public FsmObject soundTag;

        public override bool HideGameObjectReference() => true;

        protected override void DoPreProcessing() {
            base.DoPreProcessing();
            m_IPlayMethod = SetIPlayMethod();
            m_PlayMethodSelector = PlayMethodFactory();
        }

        protected virtual PlayMethodDecorator PlayMethodFactory() =>
            new PlayMethodSound(m_IPlayMethod, m_SoundEventArguments);

        protected virtual IPlayMethod SetIPlayMethod() => new IPlaySound();

        protected override SoundEventArgumentBuilder CreateSoundEventDefault() =>
            base.CreateSoundEventDefault()
                .WithSoundTag(m_SoundTag)
                .WithOwner(m_Transform)
                .Build();
    }
}
#endif
