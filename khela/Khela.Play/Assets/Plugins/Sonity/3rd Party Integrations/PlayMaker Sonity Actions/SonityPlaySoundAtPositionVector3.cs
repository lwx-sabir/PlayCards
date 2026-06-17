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
    [Tooltip("Play a Sound Event at target Vector3 position")]
    public class SonityPlaySoundAtPositionVector3 : SonityPlaySound {
        [Title("Play Sound Event at")]
        [Tooltip("Vector3 position to play Sound Event from")]
        public FsmVector3 playbackPositionVector3;

        protected override IPlayMethod SetIPlayMethod() => new IPlayAtVector3();

        protected override SoundEventArgumentBuilder CreateSoundEventDefault() =>
            base.CreateSoundEventDefault()
             .WithVector3(playbackPositionVector3.Value)
            .Build();
    }
}
#endif