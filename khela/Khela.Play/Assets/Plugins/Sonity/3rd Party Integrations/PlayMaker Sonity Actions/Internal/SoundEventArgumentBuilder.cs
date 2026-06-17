// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER
using System;
using UnityEngine;

namespace Sonity.PlayMaker.Internal {

    public class SoundEventArgumentBuilder {

        protected SoundEvent m_SoundEvent;
        protected SonityParameterContainer m_WrapperContainer;
        protected SoundTag m_SoundTag;
        protected Transform m_Owner;

        protected Vector3 m_TargetVector3;
        protected Transform m_TargetTransform;

        protected bool m_AllowFadeout;
        protected bool m_StopAllOtherMusic;

        public SoundEventArgumentBuilder Build() =>
            CanBuild() ? SafeBuild()
            : throw new InvalidOperationException("Could not build valid parameters. Sound event or Wrapper does not exist.");

        private bool CanBuild() =>
            m_SoundEvent != null;

        private SoundEventArgumentBuilder SafeBuild() =>
            this;

        public SoundEventArgumentBuilder WithWrapper(SonityParameterContainer wrapper) {
            this.m_WrapperContainer = wrapper;
            return this;
        }

        public SoundEventArgumentBuilder WithSoundEvent(SoundEvent soundEvent) {
            this.m_SoundEvent = soundEvent;
            return this;
        }

        public SoundEventArgumentBuilder WithSoundTag(SoundTag tag) {
            this.m_SoundTag = tag;
            return this;
        }

        public SoundEventArgumentBuilder WithOwner(Transform owner) {
            this.m_Owner = owner;
            return this;
        }

        public SoundEventArgumentBuilder WithTransform(Transform targetTransform) {
            this.m_TargetTransform = targetTransform;
            return this;
        }

        public SoundEventArgumentBuilder WithVector3(Vector3 targetVector3) {
            this.m_TargetVector3 = targetVector3;
            return this;
        }

        public SoundEventArgumentBuilder WithAllowFadeout(bool allowFadeout) {
            this.m_AllowFadeout = allowFadeout;
            return this;
        }

        public SoundEventArgumentBuilder WithStopAllOtherMusic(bool stop) {
            this.m_StopAllOtherMusic = stop;
            return this;
        }

#region Properties
        public SoundEvent SoundEvent => m_SoundEvent;
        public SonityParameterContainer WrapperContainer => m_WrapperContainer;

        public Transform Owner => m_Owner;
        public SoundTag SoundTag => m_SoundTag;

        public Vector3 TargetVector3 => m_TargetVector3;
        public Transform TargetTransform => m_TargetTransform;

        public bool AllowFadeout => m_AllowFadeout;
        public bool StopAllOtherMusic => m_StopAllOtherMusic;
#endregion

    }
}
#endif
