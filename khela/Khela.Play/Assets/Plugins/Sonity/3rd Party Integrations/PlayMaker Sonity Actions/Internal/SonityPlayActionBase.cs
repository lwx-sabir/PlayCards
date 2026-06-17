// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER
using HutongGames.PlayMaker;
using Sonity.Internal;
using System;
using System.Collections.Generic;
using UnityEngine;
using TooltipAttribute = HutongGames.PlayMaker.TooltipAttribute;

namespace Sonity.PlayMaker.Internal {

    public abstract class SonityPlayActionBase : SonityActionBase {

#region Private variables
        private int m_NumberOfParameters = 0;
        private int m_MaxNumberOfParameters = 12;

        private string updateModeField = "mode";
        private string paramTypeField = "type";
        private string valueField = "value";

        private List<FsmVar> cachedParameterValues = new List<FsmVar>();
#endregion

#region Protected variables
        protected SonityParameterContainer m_SonityWrapperContainer;
        protected Func<SoundEventArgumentBuilder> SoundEventArgsFactory;
        protected SoundEventArgumentBuilder m_SoundEventArguments;
        protected PlayMethodDecorator m_PlayMethodSelector;
        protected IPlayMethod m_IPlayMethod;
#endregion

#region FSM Variables
        [Tooltip("Number of Sound parameters this event should use. Max is 12.")]
        public FsmInt numberOfParameters;

        [Title("Parameter type")]
        [Tooltip("Changes parameter type of the SoundEvent. Learn more about parameter types in the Sonity Manual.")]
        [ObjectType(typeof(Sonity.Internal.SoundParameterType))]
        public FsmEnum type0, type1, type2, type3, type4, type5, type6, type7, type8, type9, type10, type11;

        [Title("Update mode")]
        [Tooltip("Set to 'Once' to make the initial value unchangable. Set to 'Continuous' to enable this parameter to be updated when the Parameter's value changes.")]
        [ObjectType(typeof(Sonity.UpdateMode))]
        public FsmEnum mode0, mode1, mode2, mode3, mode4, mode5, mode6, mode7, mode8, mode9, mode10, mode11;

        [Title("Parameter value")]
        [Tooltip("Initial value of the parameter. Can also be set to an Fsm Variable.")]
        [ObjectType(typeof(float))]
        public FsmVar value0, value1, value2, value3, value4, value5, value6, value7, value8, value9, value10, value11;

        [Tooltip("Enable Sound parameters during playback.")]
        public FsmBool useParameters;
#endregion

#region Editor variables
        [HideInInspector]
        public FsmBool showFoldOut = true;
#endregion

#region Public properties
        public PlayMethodDecorator PlayMethod {
            get => m_PlayMethodSelector;
            set => m_PlayMethodSelector = value;
        }

        public SonitySoundParameterWrapper[] SoundParameterWrappers {
            get => m_SonityWrapperContainer.SoundParameterWrappers;
        }

        public SoundParameterInternals[] SoundParameters {
            get => m_SonityWrapperContainer.ParameterInstances;
        }

        public int NumberOfParameters {
            get => m_NumberOfParameters;

            set {
                m_NumberOfParameters = Mathf.Clamp(value, 0, m_MaxNumberOfParameters);
            }
        }
#endregion

        protected virtual void DoPreProcessing() {

            NumberOfParameters = numberOfParameters.Value;
            SoundEventArgsFactory = CreateSoundEventWithParameters;

            if (!useParameters.Value) {
                NumberOfParameters = 0;
                SoundEventArgsFactory = CreateSoundEventDefault;
            } else {
                PopulateCachedParameterValues();
                m_SonityWrapperContainer = SonityPlayMakerHelper.ParameterWrapperFactory(this, paramTypeField, updateModeField, NumberOfParameters, FsmVariableValuesToArray());
                SoundEventArgsFactory = CreateSoundEventWithParameters;
            }

            m_SoundEventArguments = SoundEventArgsFactory();
        }

        public override void OnEnter() {
            //Initialize methods have to be set before PreProcess, otherwise m_SoundEvent is null.
            if (!InitializeSoundEvent())
                return;

            InitializeAndCacheTransform();

            DoPreProcessing();

            PlaySound();

            if (!HasContinuousParameter() || !useParameters.Value) {
                Finish();
            }
        }

        public override void OnUpdate() {

            if (SoundManager.Instance.GetSoundEventState(m_SoundEvent, m_Transform) == SoundEventState.NotPlaying)
                return;

            if (useParameters.Value) {
                DoSoundParameterUpdate();
            }
        }

        public override void Reset() {
            base.Reset();

#region Reset Sound parameters
            for (int i = 0; i < m_MaxNumberOfParameters; i++) {
                var fsmVar = SonityPlayMakerHelper.GetFsmVarField(this, valueField + i);
                var newVar = new FsmFloat();

                if (fsmVar != null) {
                    fsmVar.Init(newVar);
                    fsmVar.NamedVar = newVar;
                }

                var fsmParamType = SonityPlayMakerHelper.GetFsmEnumField(this, paramTypeField + i);
                if (fsmParamType != null) {
                    fsmParamType.Value = SoundParameterType.Volume;
                }

                var fsmUpdateMode = SonityPlayMakerHelper.GetFsmEnumField(this, updateModeField + i);
                if ((fsmUpdateMode != null)) {
                    fsmUpdateMode.Value = UpdateMode.Once;
                }
            }
#endregion

            fsmSoundEvent = new FsmObject();
            numberOfParameters = 0;
            useParameters = false;
        }

        /// <summary>
        /// Dynamic method for playing the Sound Event. Play method used changes based on <see cref="PlaybackType"/> of <paramref name="m_SelectedPlaybackType"/> and supplied <see cref="PlayMethodBase"/> <paramref name="m_PlayMethod"/> class.
        /// </summary>
        private void PlaySound() {
            //Debug.Log("Playing sound");
            m_PlayMethodSelector.SelectMethod();
        }

        /// <summary>
        /// Updates the values of <see cref="SoundParameterInternals">Sound Parameters</see> if the Sound UI Event is playing.
        /// </summary>
        private void PopulateCachedParameterValues() {
            cachedParameterValues.Clear();
            for (int i = 0; i < NumberOfParameters; i++) {
                FsmVar currentValue = SonityPlayMakerHelper.GetFsmVarField(this, valueField + i);
                cachedParameterValues.Add(currentValue);
            }
        }

        /// <summary>
        /// Iterates through <see cref="valueField"/>s equal to <see cref="NumberOfParameters"/>.
        /// </summary>
        /// <returns><see cref="valueField"/>s as an array of <see cref="FsmVar"/>s</returns>
        private FsmVar[] FsmVariableValuesToArray() {
            FsmVar[] temp = new FsmVar[NumberOfParameters];
            for (int i = 0; i < NumberOfParameters; i++) {
                temp[i] = SonityPlayMakerHelper.GetFsmVarField(this, valueField + i);
            }
            return temp;
        }

        protected virtual SoundEventArgumentBuilder CreateSoundEventDefault() =>
            new SoundEventArgumentBuilder()
            .WithSoundEvent(m_SoundEvent)
            .Build();

        protected virtual SoundEventArgumentBuilder CreateSoundEventWithParameters() =>
            CreateSoundEventDefault()
            .WithWrapper(m_SonityWrapperContainer)
            .Build();

        /// <summary>
        /// Updates <see cref="SoundParameterInternals">Sound Parameters</see> with new values from linked <see cref="FsmVar">Fsm Variables</see>.
        /// </summary>
        public void UpdateParameterValues() {
            for (int i = 0; i < NumberOfParameters; i++) {
                //Debug.Log("Updated parameters for " + m_SoundEvent.name);
                //Maybe better to cache the property fields somewhere so they can be accessed without recursion here.
                FsmVar currentValue = SonityPlayMakerHelper.GetFsmVarField(this, valueField + i);

                if (cachedParameterValues[i].GetValue() == currentValue.GetValue()) {
                    continue;
                }

                currentValue.UpdateValue();

                var newValue = currentValue.GetValue();
                cachedParameterValues[i].SetValue(newValue);
                SoundParameterWrappers[i].Value = newValue;
            }
        }

        /// <summary>
        /// Updates the values of <see cref="SoundParameterInternals">Sound Parameters</see> if the Sound Event is playing.
        /// </summary>
        public virtual void DoSoundParameterUpdate() {
            UpdateParameterValues();
        }

        /// <summary>
        /// Iterates through all <see cref="SoundParameterInternals"/>, evaluating <see cref="UpdateMode"/>.
        /// </summary>
        /// <returns> True if any <see cref="SoundParameterInternals"/> is set to <see cref="UpdateMode.Continuous"/>.
        /// False if all <see cref="SoundParameterInternals"/> are set to <see cref="UpdateMode.Once"/>.
        /// </returns>

        public bool HasContinuousParameter() {
            for (int i = 0; i < NumberOfParameters; i++) {
                FsmEnum fsmEnum = SonityPlayMakerHelper.GetFsmEnumField(this, updateModeField + i);
                if ((UpdateMode)fsmEnum.Value == UpdateMode.Continuous)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// This sets the values of all relevant <see cref="FsmVar"/>s to match the type expected by the <see cref="SoundParameterInternals"> SoundParameter</see>. Used by Editor Script.
        /// </summary>
        public void SetFsmVarToSoundParameter() {
            for (int i = 0; i < NumberOfParameters; i++) {
                FsmVar fsmVar = SonityPlayMakerHelper.GetFsmVarField(this, valueField + i);
                FsmEnum fsmEnum = SonityPlayMakerHelper.GetFsmEnumField(this, paramTypeField + i);
                NamedVariable type;

                if (fsmVar != null || fsmEnum != null) {
                    type = SonityPlayMakerHelper.GetFsmTypeFromParameter((SoundParameterType)fsmEnum.Value);
                    if (type.VariableType != fsmVar.Type) {
                        fsmVar.Init(type);
                        fsmVar.NamedVar = type;
                    }
                }
            }
        }
    }
}
#endif
