// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

namespace Sonity.PlayMaker.Internal {

    public class IPlayMusic : IPlayMethod {

        public void PlaySound(SoundEventArgumentBuilder parameters) {
            parameters.SoundEvent.MusicPlay(
                parameters.StopAllOtherMusic,
                parameters.AllowFadeout);
        }

        public void PlaySoundWithParameters(SoundEventArgumentBuilder parameters) {
            parameters.SoundEvent.MusicPlay(
                parameters.StopAllOtherMusic,
                parameters.AllowFadeout,
                parameters.WrapperContainer.ParameterInstances);
        }

        public void PlaySoundWithTag(SoundEventArgumentBuilder parameters) => PlaySound(parameters);
        public void PlaySoundWithTagAndParameters(SoundEventArgumentBuilder parameters) => PlaySound(parameters);
    }
}
#endif