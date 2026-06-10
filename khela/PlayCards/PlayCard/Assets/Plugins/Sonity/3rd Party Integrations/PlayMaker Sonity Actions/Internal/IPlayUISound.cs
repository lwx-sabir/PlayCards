// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

namespace Sonity.PlayMaker.Internal {

    public class IPlayUISound : IPlayMethod {

        public void PlaySound(SoundEventArgumentBuilder parameters) {
            parameters.SoundEvent.UIPlay();
        }

        public void PlaySoundWithTag(SoundEventArgumentBuilder parameters) {

            parameters.SoundEvent.UIPlay(parameters.SoundTag);
        }

        public void PlaySoundWithParameters(SoundEventArgumentBuilder parameters) {
            parameters.SoundEvent.UIPlay(parameters.WrapperContainer.ParameterInstances);
        }

        public void PlaySoundWithTagAndParameters(SoundEventArgumentBuilder parameters) {
            parameters.SoundEvent.UIPlay(parameters.SoundTag, parameters.WrapperContainer.ParameterInstances);
        }
    }
}
#endif