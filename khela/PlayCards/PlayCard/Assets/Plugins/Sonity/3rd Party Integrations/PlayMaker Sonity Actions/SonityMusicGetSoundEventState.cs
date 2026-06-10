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
    [Tooltip("Gets the current SoundEventState of the reference Sound Event.")]
    public class SonityMusicGetSoundEventState : SonityActionBase {
        [Tooltip("Variable to store the state of the Sound Event. Can be Playing, Pause, Delay or NotPlaying.")]
        [UIHint(UIHint.Variable)]
        [ObjectType(typeof(SoundEventState))]
        public FsmEnum storeStateIn;

        public override bool HideGameObjectReference() => true;

        protected override void DoSoundEventAction() =>
            storeStateIn.Value = m_SoundEvent.MusicGetSoundEventState();
    }
}
#endif