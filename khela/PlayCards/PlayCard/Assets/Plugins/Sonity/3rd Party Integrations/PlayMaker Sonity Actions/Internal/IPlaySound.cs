// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

namespace Sonity.PlayMaker.Internal {

    public class IPlaySound : IPlayMethod {

        public void PlaySound(SoundEventArgumentBuilder parameters) {
            parameters.SoundEvent.Play(parameters.Owner);
        }

        public void PlaySoundWithTag(SoundEventArgumentBuilder parameters) {
            parameters.SoundEvent.Play(parameters.Owner, parameters.SoundTag);
        }

        public void PlaySoundWithParameters(SoundEventArgumentBuilder parameters) {
            parameters.SoundEvent.Play(parameters.Owner, parameters.WrapperContainer.ParameterInstances);
        }

        public void PlaySoundWithTagAndParameters(SoundEventArgumentBuilder parameters) {
            parameters.SoundEvent.Play(parameters.Owner, parameters.SoundTag, parameters.WrapperContainer.ParameterInstances);
        }
    }
}
#endif