// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

namespace Sonity.PlayMaker.Internal {

    public interface IPlayMethod {

        public void PlaySound(SoundEventArgumentBuilder parameters);

        public void PlaySoundWithTag(SoundEventArgumentBuilder parameters);

        public void PlaySoundWithParameters(SoundEventArgumentBuilder parameters);

        public void PlaySoundWithTagAndParameters(SoundEventArgumentBuilder parameters);
    }
}
#endif