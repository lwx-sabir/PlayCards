// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

namespace Sonity.PlayMaker.Internal {

    public class PlayMethodSound : PlayMethodDecorator {

        private IPlayMethod m_PlayMethod;
        public PlayMethodSound(IPlayMethod method, SoundEventArgumentBuilder arguments) : base(arguments) {
            m_PlayMethod = method;
        }

        public IPlayMethod PlayMethod { get => m_PlayMethod; }

        public override void SelectMethod() {
            if (HasTag() && HasParameters()) {
                m_PlayMethod.PlaySoundWithTagAndParameters(soundEventArgs);
                return;
            }

            if (HasTag()) {
                m_PlayMethod.PlaySoundWithTag(soundEventArgs);
                return;
            }

            if (HasParameters()) {
                m_PlayMethod.PlaySoundWithParameters(soundEventArgs);
                return;
            }

            m_PlayMethod.PlaySound(soundEventArgs);
        }
    }
}
#endif