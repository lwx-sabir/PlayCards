// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER
using HutongGames.PlayMaker;
using Sonity.Internal;
using System;
using System.Reflection;

namespace Sonity.PlayMaker.Internal {

    public class SonitySoundParameterWrapper {

        private SoundParameterInternals m_SoundParameterInternals = new SoundParameterInternals();
        private PropertyInfo m_PropertyField;

        public SonitySoundParameterWrapper(SoundParameterInternals parameter) {
            m_SoundParameterInternals = SonityPlayMakerHelper.GetsSoundParameterFrom(parameter);
            var fieldName = SonityPlayMakerHelper.GetPropertyFieldName(m_SoundParameterInternals);

            if (m_SoundParameterInternals.GetType().GetProperty(fieldName) != null) {
                m_PropertyField = m_SoundParameterInternals.GetType().GetProperty(fieldName);
            } else {
                throw new ArgumentException($"Property {fieldName} not found.");
            }
        }

        public SonitySoundParameterWrapper(object source, string paramTypeFieldName, string updateModeFieldName) {
            m_SoundParameterInternals = SonityPlayMakerHelper.GetSoundParameterFrom(GetTypeFromFsm(source, paramTypeFieldName));

            SetUpdateModeFromFsm(source, updateModeFieldName);

            var fieldName = SonityPlayMakerHelper.GetPropertyFieldName(m_SoundParameterInternals);

            if (m_SoundParameterInternals.GetType().GetProperty(fieldName) != null) {
                m_PropertyField = m_SoundParameterInternals.GetType().GetProperty(fieldName);
            } else {
                throw new ArgumentException($"Property {fieldName} not found.");
            }
        }

        public SoundParameterInternals ParameterInstance {
            get { return m_SoundParameterInternals; }
            set { m_SoundParameterInternals = value; }
        }

        public object Value {
            get {
                return m_PropertyField.GetValue(m_SoundParameterInternals);
            }

            set {
                if (value.GetType() == m_PropertyField.PropertyType) {
                    m_PropertyField.SetValue(m_SoundParameterInternals, value);
                }
            }
        }

        public SoundParameterType Type {
            get => m_SoundParameterInternals.internals.type;
            set => m_SoundParameterInternals.internals.type = value;
        }

        public UpdateMode UpdateMode {
            get => m_SoundParameterInternals.internals.updateMode;
            set => m_SoundParameterInternals.internals.updateMode = value;
        }

        public SoundParameterType GetTypeFromFsm(object source, string fieldName) {

            FsmEnum fsmInfo = (FsmEnum)source.GetType().GetField(fieldName).GetValue(source);
            SoundParameterType parameterType = (SoundParameterType)fsmInfo.Value;

            if (!fsmInfo.IsNone)
                return parameterType;
            else
                return SoundParameterType.Volume;

        }

        public void SetUpdateModeFromFsm(object source, string fieldName) {
            FsmEnum fsmInfo = (FsmEnum)source.GetType().GetField(fieldName).GetValue(source);
            UpdateMode updateValue = (UpdateMode)fsmInfo.Value;
            if (!fsmInfo.IsNone) m_SoundParameterInternals.internals.updateMode = updateValue;
        }
    }
}
#endif