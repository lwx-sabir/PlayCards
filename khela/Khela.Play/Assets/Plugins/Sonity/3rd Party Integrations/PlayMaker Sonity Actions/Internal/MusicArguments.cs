// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

namespace Sonity.PlayMaker.Internal {

    public class MusicArguments {

        private bool m_AllowFadeout;
        private bool m_StopAllOtherMusic;

        public MusicArguments(bool fadeout = true, bool stopOther = true) {
            m_AllowFadeout = fadeout;
            m_StopAllOtherMusic = stopOther;
        }

 #region Properties
        public bool AllowFadeout { get => m_AllowFadeout; set => m_AllowFadeout = value; }
        public bool StopAllOtherMusic { get => m_StopAllOtherMusic; set => m_StopAllOtherMusic = value; }
#endregion
    }
}
#endif
