// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

namespace Sonity.PlayMaker.Internal {

    public class PlayMethodMusic : PlayMethodDecorator {

        private IPlayMethod m_PlayMethod;

        public PlayMethodMusic(IPlayMethod method, SoundEventArgumentBuilder arguments) : base(arguments) {
            m_PlayMethod = method;
        }

        public override void SelectMethod() {
            if (HasParameters()) {
                m_PlayMethod.PlaySoundWithParameters(soundEventArgs);
                return;
            }

            m_PlayMethod.PlaySound(soundEventArgs);
        }
    }
}
#endif