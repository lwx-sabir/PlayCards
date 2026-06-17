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
    [Tooltip("Play a Sound Event at target Transform")]
    public class SonityPlaySoundAtPositionTransform : SonityPlaySound {
        [Title("Play Sound Event at")]
        [Tooltip("Target Transform to play Sound Event from")]
        public FsmGameObject playbackPositionTransform;

        protected override IPlayMethod SetIPlayMethod() => new IPlayAtTransform();

        protected override SoundEventArgumentBuilder CreateSoundEventDefault() =>
            base.CreateSoundEventDefault()
            .WithTransform(playbackPositionTransform.Value.transform)
            .Build();
    }
}
#endif
