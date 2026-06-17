// PlayMaker integration by Simon Palmblad
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if SONITY_ENABLE_INTEGRATION_PLAYMAKER
using HutongGames.PlayMaker;
using Sonity.Internal;
using System;
using System.Reflection;

namespace Sonity.PlayMaker.Internal {

    public enum ParameterValueType { Float, Bool, Int };

    public static class SonityPlayMakerHelper {

        public static SoundParameterInternals GetsSoundParameterFrom(SoundParameterInternals param) => param.internals.type switch {
#region Floats
            SoundParameterType.Volume => new SoundParameterVolumeRatio(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.Pitch => new SoundParameterPitchSemitone(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.Delay => new SoundParameterDelay(param.internals.valueFloat),
            SoundParameterType.Increase2D => new SoundParameterIncrease2D(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.Intensity => new SoundParameterIntensity(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.ReverbZoneMix => new SoundParameterReverbZoneMixDecibel(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.StartPosition => new SoundParameterReverbZoneMixRatio(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.StereoPan => new SoundParameterStartPosition(param.internals.valueFloat),
            SoundParameterType.DistanceScale => new SoundParameterDistanceScale(param.internals.valueFloat),
            SoundParameterType.DistortionIncrease => new SoundParameterDistortionIncrease(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.FadeInLength => new SoundParameterFadeInLength(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.FadeInShape => new SoundParameterFadeInShape(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.FadeOutLength => new SoundParameterFadeOutLength(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.FadeOutShape => new SoundParameterFadeOutShape(param.internals.valueFloat, param.internals.updateMode),
#endregion

#region Bools
            SoundParameterType.Reverse => new SoundParameterReverse(param.internals.valueBool, param.internals.updateMode),
            SoundParameterType.FollowPosition => new SoundParameterPitchSemitone(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.BypassReverbZones => new SoundParameterPitchSemitone(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.BypassVoiceEffects => new SoundParameterPitchSemitone(param.internals.valueFloat, param.internals.updateMode),
            SoundParameterType.BypassListenerEffects => new SoundParameterPitchSemitone(param.internals.valueFloat, param.internals.updateMode),
#endregion

            SoundParameterType.Polyphony => new SoundParameterPolyphony(param.internals.valueInt),
            _ => throw new ArgumentOutOfRangeException(nameof(param.internals.type), $"Not expected type value: {param.internals.type}"),
        };

        public static SoundParameterInternals GetSoundParameterFrom(SoundParameterType paramType) => paramType switch {
#region Floats
            SoundParameterType.Volume => new SoundParameterVolumeRatio(),
            SoundParameterType.Pitch => new SoundParameterPitchSemitone(),
            SoundParameterType.Delay => new SoundParameterDelay(),
            SoundParameterType.Increase2D => new SoundParameterIncrease2D(),
            SoundParameterType.Intensity => new SoundParameterIntensity(),
            SoundParameterType.ReverbZoneMix => new SoundParameterReverbZoneMixDecibel(),
            SoundParameterType.StartPosition => new SoundParameterReverbZoneMixRatio(),
            SoundParameterType.StereoPan => new SoundParameterStartPosition(),
            SoundParameterType.DistanceScale => new SoundParameterDistanceScale(),
            SoundParameterType.DistortionIncrease => new SoundParameterDistortionIncrease(),
            SoundParameterType.FadeInLength => new SoundParameterFadeInLength(),
            SoundParameterType.FadeInShape => new SoundParameterFadeInShape(),
            SoundParameterType.FadeOutLength => new SoundParameterFadeOutLength(),
            SoundParameterType.FadeOutShape => new SoundParameterFadeOutShape(),
#endregion

#region Bools
            SoundParameterType.Reverse => new SoundParameterReverse(),
            SoundParameterType.FollowPosition => new SoundParameterPitchSemitone(),
            SoundParameterType.BypassReverbZones => new SoundParameterPitchSemitone(),
            SoundParameterType.BypassVoiceEffects => new SoundParameterPitchSemitone(),
            SoundParameterType.BypassListenerEffects => new SoundParameterPitchSemitone(),
#endregion
            SoundParameterType.Polyphony => new SoundParameterPolyphony(),
            _ => throw new ArgumentOutOfRangeException(nameof(paramType), $"Not expected type value: {paramType}"),
        };

        public static string GetPropertyFieldName(SoundParameterInternals param) => param.internals.type switch {
#region Floats
            SoundParameterType.Volume => nameof(SoundParameterVolumeRatio.VolumeRatio),
            SoundParameterType.Pitch => "PitchSemitone",
            SoundParameterType.Delay => "Delay",
            SoundParameterType.Increase2D => "Increase2D",
            SoundParameterType.Intensity => "Intensity",
            SoundParameterType.ReverbZoneMix => "ReverbZoneMixDecibel",
            SoundParameterType.StartPosition => "StartPosition",
            SoundParameterType.StereoPan => "StereoPan",
            SoundParameterType.DistanceScale => "DistanceScale",
            SoundParameterType.DistortionIncrease => "DistortionIncrease",
            SoundParameterType.FadeInLength => "FadeInLength",
            SoundParameterType.FadeInShape => "FadeInShape",
            SoundParameterType.FadeOutLength => "FadeOutLength",
            SoundParameterType.FadeOutShape => "FadeOutShape",
#endregion

#region Bools
            SoundParameterType.Reverse => "Reverse",
            SoundParameterType.FollowPosition => "FollowPosition",
            SoundParameterType.BypassReverbZones => "BypassReverbZones",
            SoundParameterType.BypassVoiceEffects => "BypassVoiceEffects",
            SoundParameterType.BypassListenerEffects => "BypassVoiceEffects",
#endregion
            SoundParameterType.Polyphony => "Polyphony",
            _ => throw new ArgumentOutOfRangeException(nameof(param.internals.type), $"Not expected type value: {param.internals.type}"),
        };

        public static ParameterValueType GetTypeValueFormat(SoundParameterType type) => type switch {

#region Floats
            SoundParameterType.Volume => ParameterValueType.Float,
            SoundParameterType.Pitch => ParameterValueType.Float,
            SoundParameterType.Delay => ParameterValueType.Float,
            SoundParameterType.Increase2D => ParameterValueType.Float,
            SoundParameterType.Intensity => ParameterValueType.Float,
            SoundParameterType.ReverbZoneMix => ParameterValueType.Float,
            SoundParameterType.StartPosition => ParameterValueType.Float,
            SoundParameterType.StereoPan => ParameterValueType.Float,
            SoundParameterType.DistanceScale => ParameterValueType.Float,
            SoundParameterType.DistortionIncrease => ParameterValueType.Float,
            SoundParameterType.FadeInLength => ParameterValueType.Float,
            SoundParameterType.FadeInShape => ParameterValueType.Float,
            SoundParameterType.FadeOutLength => ParameterValueType.Float,
            SoundParameterType.FadeOutShape => ParameterValueType.Float,
#endregion

#region Bools
            SoundParameterType.Reverse => ParameterValueType.Bool,
            SoundParameterType.FollowPosition => ParameterValueType.Bool,
            SoundParameterType.BypassReverbZones => ParameterValueType.Bool,
            SoundParameterType.BypassVoiceEffects => ParameterValueType.Bool,
            SoundParameterType.BypassListenerEffects => ParameterValueType.Bool,
#endregion
            SoundParameterType.Polyphony => ParameterValueType.Int,
            _ => throw new ArgumentOutOfRangeException(nameof(type), $"Not expected type value: {type}"),
        };

        public static SonitySoundParameterWrapper[] WrapSoundParameters(object source, string paramTypeField, string updateModeField, int numberOfParameters, FsmVar[] parameterValues) {
            SonitySoundParameterWrapper[] tempWrapper = new SonitySoundParameterWrapper[numberOfParameters];

            for (int i = 0; i < numberOfParameters; i++) {
                tempWrapper[i] = new SonitySoundParameterWrapper(source, paramTypeField + i, updateModeField + i);
                tempWrapper[i].Value = parameterValues[i].GetValue();
            }

            return tempWrapper;
        }

        public static SonityParameterContainer ParameterWrapperFactory(object source, string paramTypeField, string updateModeField, int numberOfParameters, FsmVar[] parameterValues)
            => new SonityParameterContainer(WrapSoundParameters(source, paramTypeField, updateModeField, numberOfParameters, parameterValues));

        public static NamedVariable GetFsmTypeFromParameter(SoundParameterType paramType) => paramType switch {
#region Floats
            SoundParameterType.Volume => new FsmFloat(),
            SoundParameterType.Pitch => new FsmFloat(),
            SoundParameterType.Delay => new FsmFloat(),
            SoundParameterType.Increase2D => new FsmFloat(),
            SoundParameterType.Intensity => new FsmFloat(),
            SoundParameterType.ReverbZoneMix => new FsmFloat(),
            SoundParameterType.StartPosition => new FsmFloat(),
            SoundParameterType.StereoPan => new FsmFloat(),
            SoundParameterType.DistanceScale => new FsmFloat(),
            SoundParameterType.DistortionIncrease => new FsmFloat(),
            SoundParameterType.FadeInLength => new FsmFloat(),
            SoundParameterType.FadeInShape => new FsmFloat(),
            SoundParameterType.FadeOutLength => new FsmFloat(),
            SoundParameterType.FadeOutShape => new FsmFloat(),
#endregion

#region Bools
            SoundParameterType.Reverse => new FsmBool(),
            SoundParameterType.FollowPosition => new FsmBool(),
            SoundParameterType.BypassReverbZones => new FsmBool(),
            SoundParameterType.BypassVoiceEffects => new FsmBool(),
            SoundParameterType.BypassListenerEffects => new FsmBool(),
#endregion
            SoundParameterType.Polyphony => new FsmInt(),
            _ => new FsmInt(),
        };

        /// <summary>
        /// Find an <see cref="FsmEnum"/> field within this script from a string using recursion.
        /// </summary>
        /// <param name="fieldName"></param>
        /// <returns><see cref="FsmEnum"/></returns>
        public static FsmEnum GetFsmEnumField(object target, string fieldName) =>
            (FsmEnum)GetFsmFieldInfo(target, fieldName).GetValue(target);

        /// <summary>
        /// Find an <see cref="FsmVar"/> field within this script from a string using recursion.
        /// </summary>
        /// <param name="fieldName"></param>
        /// <returns><see cref="FsmVar"/></returns>
        public static FsmVar GetFsmVarField(object target, string fieldName) =>
            (FsmVar)GetFsmFieldInfo(target, fieldName).GetValue(target);

        public static FieldInfo GetFsmFieldInfo(object target, string fieldName) =>
            target.GetType().GetField(fieldName);
    }
}
#endif