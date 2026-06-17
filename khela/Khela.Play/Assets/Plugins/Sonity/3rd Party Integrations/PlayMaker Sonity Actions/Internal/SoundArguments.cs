// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER
using UnityEngine;

namespace Sonity.PlayMaker.Internal {
    public class SoundArguments {

        private SoundTag m_SoundTag;
        private Transform m_Owner;
        private Vector3 m_TargetVector3;
        private Transform m_TargetTransform;

        public SoundArguments(Transform owner, SoundTag tag) {
            m_Owner = owner;
            m_SoundTag = tag;
        }

        public SoundArguments(Transform owner, SoundTag tag, Vector3 target = default) {
            m_Owner = owner;
            m_SoundTag = tag;
            m_TargetVector3 = target;
        }

        public SoundArguments(Transform owner, SoundTag tag, Transform target = null) {
            m_Owner = owner;
            m_SoundTag = tag;
            m_TargetTransform = target;
        }

#region Properties
        public Transform Owner {
            get => m_Owner;
            set => m_Owner = value;
        }

        public SoundTag SoundTag {
            get => m_SoundTag;
            set => m_SoundTag = value;
        }

        public Vector3 TargetVector3 {
            get => m_TargetVector3;
            set => m_TargetVector3 = value;
        }

        public Transform TargetTransform {
            get => m_TargetTransform;
            set => m_TargetTransform = value;
        }
#endregion
    }
}
#endif