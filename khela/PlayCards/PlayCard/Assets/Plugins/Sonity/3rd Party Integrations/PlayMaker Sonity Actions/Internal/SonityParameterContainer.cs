// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER

using Sonity.Internal;
using System;

namespace Sonity.PlayMaker.Internal {

    public class SonityParameterContainer {

        private SonitySoundParameterWrapper[] m_SoundParameterWrappers = new SonitySoundParameterWrapper[0];
        private SoundParameterInternals[] m_SoundParameterInternals = new SoundParameterInternals[0];

        public SonityParameterContainer(SonitySoundParameterWrapper parameterWrapper) {
            m_SoundParameterWrappers[0] = parameterWrapper;
            m_SoundParameterInternals[0] = m_SoundParameterWrappers[0].ParameterInstance;
        }

        public SonityParameterContainer(SonitySoundParameterWrapper[] parameterWrappers) {
            Array.Resize(ref m_SoundParameterWrappers, parameterWrappers.Length);
            Array.Resize(ref m_SoundParameterInternals, parameterWrappers.Length);

            Array.Copy(parameterWrappers, m_SoundParameterWrappers, parameterWrappers.Length);

            for (int i = 0; i < m_SoundParameterWrappers.Length; i++) {
                m_SoundParameterInternals[i] = m_SoundParameterWrappers[i].ParameterInstance;
            }
        }

        public SoundParameterInternals[] ParameterInstances {
            get => m_SoundParameterInternals;
            set => m_SoundParameterInternals = value;
        }

        public SonitySoundParameterWrapper[] SoundParameterWrappers {
            get => m_SoundParameterWrappers;
            set => m_SoundParameterWrappers = value;
        }

        public SoundParameterInternals GetParameterInstance(int index) {
            if (index > m_SoundParameterInternals.Length)
                return m_SoundParameterInternals[index];

            else throw new ArgumentOutOfRangeException($"Index {index} is out of bounds");
        }

        public SonitySoundParameterWrapper GetWrapperInstance(int index) {
            if (index > m_SoundParameterWrappers.Length)
                return m_SoundParameterWrappers[index];

            else throw new ArgumentOutOfRangeException($"Index {index} is out of bounds");
        }
    }
}
#endif