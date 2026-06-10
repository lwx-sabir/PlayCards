// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System;
using System.Linq;

// Sonity Steam Audio
#if SONITY_ENABLE_INTEGRATION_STEAM_AUDIO
using SteamAudio;
#endif

namespace Sonity.Internal {

    public abstract class SoundContainerEditorBase : Editor {

        public SoundContainerBase mTarget;
        public SoundContainerBase[] mTargets;

        public SerializedProperty steamAudio;
        public SerializedProperty steamAudioExpand;

        // Steam Audio values
        SerializedProperty mDirectBinaural;
        SerializedProperty mInterpolation;
        SerializedProperty mPerspectiveCorrection;
        SerializedProperty mDistanceAttenuation;
        SerializedProperty mDistanceAttenuationInput;
        SerializedProperty mAirAbsorption;
        SerializedProperty mAirAbsorptionInput;
        SerializedProperty mAirAbsorptionLow;
        SerializedProperty mAirAbsorptionMid;
        SerializedProperty mAirAbsorptionHigh;
        SerializedProperty mDirectivity;
        SerializedProperty mDirectivityInput;
        SerializedProperty mDipoleWeight;
        SerializedProperty mDipolePower;
        SerializedProperty mDirectivityValue;
        SerializedProperty mOcclusion;
        SerializedProperty mOcclusionInput;
        SerializedProperty mOcclusionType;
        SerializedProperty mOcclusionRadius;
        SerializedProperty mOcclusionSamples;
        SerializedProperty mOcclusionValue;
        SerializedProperty mTransmission;
        SerializedProperty mTransmissionType;
        SerializedProperty mTransmissionInput;
        SerializedProperty mTransmissionLow;
        SerializedProperty mTransmissionMid;
        SerializedProperty mTransmissionHigh;
        SerializedProperty mTransmissionRays;
        SerializedProperty mDirectMixLevel;
        SerializedProperty mReflections;
        SerializedProperty mReflectionsType;
        SerializedProperty mUseDistanceCurveForReflections;
        SerializedProperty mCurrentBakedSource;
        SerializedProperty mApplyHRTFToReflections;
        SerializedProperty mReflectionsMixLevel;
        SerializedProperty mPathing;
        SerializedProperty mPathingProbeBatch;
        SerializedProperty mPathValidation;
        SerializedProperty mFindAlternatePaths;
        SerializedProperty mApplyHRTFToPathing;
        SerializedProperty mPathingMixLevel;

        public EditorPreviewControls previewEditorSetting = new EditorPreviewControls();

        public SoundContainerEditorCurveDraw curveDraw;
        public SoundContainerEditorPreview preview;
        public SoundContainerEditorFindAssets updateAudioClips;

        public SerializedProperty previewAudioMixerGroup;

        public SerializedProperty expandPreview;
        public SerializedProperty expandAudioClips;
        public SerializedProperty settingsExpandBase;
        public SerializedProperty settingsExpandAdvanced;
        public SerializedProperty previewCurves;

        public SerializedProperty audioClips;

        public SerializedProperty assetGuid;

        public SerializedProperty internals;
        public SerializedProperty data;

        public SerializedProperty notes;

        public SerializedProperty foundReferences;

        public SerializedProperty neverStealVoice;
        public SerializedProperty neverStealVoiceEffects;

        public SerializedProperty startPosition;

        public SerializedProperty reverse;

        public SerializedProperty dopplerAmount;

        public SerializedProperty reverbZoneMixExpand;
        public SerializedProperty reverbZoneMixDecibel;
        public SerializedProperty reverbZoneMixRatio;

        public SerializedProperty reverbZoneMixDistanceIncrease;
        public SerializedProperty reverbZoneMixDistanceRolloff;
        public SerializedProperty reverbZoneMixDistanceCurve;

        public SerializedProperty reverbZoneMixIntensityEnable;
        public SerializedProperty reverbZoneMixIntensityRolloff;
        public SerializedProperty reverbZoneMixIntensityAmount;
        public SerializedProperty reverbZoneMixIntensityCurve;

        public SerializedProperty stereoPanExpand;
        public SerializedProperty stereoPanOffset;
        public SerializedProperty stereoPanAngleUse;
        public SerializedProperty stereoPanAngleAmount;
        public SerializedProperty stereoPanAngleRolloff;

        public SerializedProperty bypassReverbZones;
        public SerializedProperty bypassVoiceEffects;
        public SerializedProperty bypassListenerEffects;

        public SerializedProperty hrtfPluginSpatialize;
        public SerializedProperty hrtfPluginSpatializePostEffects;

        public SerializedProperty audioMixerGroup;
        public SerializedProperty preventEndClicks;
        public SerializedProperty loopEnabled;
        public SerializedProperty followPosition;
        public SerializedProperty randomStartPosition;
        public SerializedProperty randomStartPositionMin;
        public SerializedProperty randomStartPositionMax;
        public SerializedProperty stopIfTransformIsNull;
        //public SerializedProperty virtualize; // Virtualize Todo

        public SerializedProperty priority;

        public SerializedProperty lockAxisEnable;
        public SerializedProperty lockAxis;
        public SerializedProperty lockAxisPosition;
        public SerializedProperty playOrder;

        public SerializedProperty distanceEnabled;
        public SerializedProperty distanceScale;

        public SerializedProperty volumeExpand;

        public SerializedProperty volumeDecibel;
        public SerializedProperty volumeRatio;

        public SerializedProperty volumeRandomEnable;
        public SerializedProperty volumeRandomRangeDecibel;

        public SerializedProperty volumeDistanceRolloff;
        public SerializedProperty volumeDistanceCurve;

        public SerializedProperty volumeIntensityEnable;
        public SerializedProperty volumeIntensityRolloff;
        public SerializedProperty volumeIntensityStrength;
        public SerializedProperty volumeIntensityCurve;

        public SerializedProperty volumeDistanceCrossfadeEnable;
        public SerializedProperty volumeDistanceCrossfadeTotalLayersOneBased;
        public SerializedProperty volumeDistanceCrossfadeLayerOneBased;
        public SerializedProperty volumeDistanceCrossfadeTotalLayers;
        public SerializedProperty volumeDistanceCrossfadeLayer;
        public SerializedProperty volumeDistanceCrossfadeRolloff;
        public SerializedProperty volumeDistanceCrossfadeCurve;

        public SerializedProperty volumeIntensityCrossfadeEnable;
        public SerializedProperty volumeIntensityCrossfadeTotalLayersOneBased;
        public SerializedProperty volumeIntensityCrossfadeLayerOneBased;
        public SerializedProperty volumeIntensityCrossfadeTotalLayers;
        public SerializedProperty volumeIntensityCrossfadeLayer;
        public SerializedProperty volumeIntensityCrossfadeRolloff;
        public SerializedProperty volumeIntensityCrossfadeCurve;

        public SerializedProperty spatialBlendExpand;
        public SerializedProperty spatialBlend;

        public SerializedProperty spatialBlendDistanceRolloff;
        public SerializedProperty spatialBlendDistance3DIncrease;
        public SerializedProperty spatialBlendDistanceCurve;

        public SerializedProperty spatialBlendIntensityEnable;
        public SerializedProperty spatialBlendIntensityRolloff;
        public SerializedProperty spatialBlendIntensityStrength;
        public SerializedProperty spatialBlendIntensityCurve;

        public SerializedProperty spatialSpreadExpand;
        public SerializedProperty spatialSpreadDegrees;
        public SerializedProperty spatialSpreadRatio;

        public SerializedProperty spatialSpreadDistanceRolloff;
        public SerializedProperty spatialSpreadDistanceCurve;

        public SerializedProperty spatialSpreadIntensityEnable;
        public SerializedProperty spatialSpreadIntensityRolloff;
        public SerializedProperty spatialSpreadIntensityStrength;
        public SerializedProperty spatialSpreadIntensityCurve;

        public SerializedProperty distortionExpand;
        public SerializedProperty distortionEnabled;
        public SerializedProperty distortionAmount;

        public SerializedProperty distortionDistanceEnable;
        public SerializedProperty distortionDistanceRolloff;
        public SerializedProperty distortionDistanceCurve;

        public SerializedProperty distortionIntensityEnable;
        public SerializedProperty distortionIntensityRolloff;
        public SerializedProperty distortionIntensityStrength;
        public SerializedProperty distortionIntensityCurve;

        public SerializedProperty lowpassExpand;
        public SerializedProperty lowpassEnabled;
        public SerializedProperty lowpassFrequencyEditor;
        public SerializedProperty lowpassFrequencyEngine;
        public SerializedProperty lowpassAmountEditor;
        public SerializedProperty lowpassAmountEngine;

        public SerializedProperty lowpassDistanceEnable;
        public SerializedProperty lowpassDistanceFrequencyRolloff;
        public SerializedProperty lowpassDistanceFrequencyCurve;
        public SerializedProperty lowpassDistanceAmountRolloff;
        public SerializedProperty lowpassDistanceAmountCurve;

        public SerializedProperty lowpassIntensityEnable;
        public SerializedProperty lowpassIntensityFrequencyRolloff;
        public SerializedProperty lowpassIntensityFrequencyStrength;
        public SerializedProperty lowpassIntensityFrequencyCurve;
        public SerializedProperty lowpassIntensityAmountRolloff;
        public SerializedProperty lowpassIntensityAmountStrength;
        public SerializedProperty lowpassIntensityAmountCurve;

        public SerializedProperty highpassExpand;
        public SerializedProperty highpassEnabled;
        public SerializedProperty highpassFrequencyEditor;
        public SerializedProperty highpassFrequencyEngine;
        public SerializedProperty highpassAmountEditor;
        public SerializedProperty highpassAmountEngine;

        public SerializedProperty highpassDistanceEnable;
        public SerializedProperty highpassDistanceFrequencyRolloff;
        public SerializedProperty highpassDistanceFrequencyCurve;
        public SerializedProperty highpassDistanceAmountRolloff;
        public SerializedProperty highpassDistanceAmountCurve;

        public SerializedProperty highpassIntensityEnable;
        public SerializedProperty highpassIntensityFrequencyRolloff;
        public SerializedProperty highpassIntensityFrequencyStrength;
        public SerializedProperty highpassIntensityFrequencyCurve;
        public SerializedProperty highpassIntensityAmountRolloff;
        public SerializedProperty highpassIntensityAmountStrength;
        public SerializedProperty highpassIntensityAmountCurve;

        public SerializedProperty pitchExpand;
        public SerializedProperty pitchSemitoneEditor;
        public SerializedProperty pitchRatio;
        public SerializedProperty pitchRandomEnable;
        public SerializedProperty pitchRandomRangeSemitone;

        public SerializedProperty pitchIntensityEnable;
        public SerializedProperty pitchIntensityLowSemitone;
        public SerializedProperty pitchIntensityLowRatio;
        public SerializedProperty pitchIntensityHighSemitone;
        public SerializedProperty pitchIntensityHighRatio;
        public SerializedProperty pitchIntensityRangeSemitone;
        public SerializedProperty pitchIntensityRangeRatio;
        public SerializedProperty pitchIntensityBaseSemitone;
        public SerializedProperty pitchIntensityBaseRatio;
        public SerializedProperty pitchIntensityRolloff;
        public SerializedProperty pitchIntensityCurve;

        private float guiCurveHeight = 25f;

        [NonSerialized]
        private bool initialized;

        // The material to use when drawing with OpenGL
        public UnityEngine.Material cachedMaterial;

        private void OnEnable() {
            // Cache the "Hidden/Internal-Colored" shader
            cachedMaterial = new UnityEngine.Material(Shader.Find("Hidden/Internal-Colored"));
        }

        private void FindProperties() {

            assetGuid = serializedObject.FindProperty(nameof(SoundContainerBase.assetGuid));

            internals = serializedObject.FindProperty(nameof(SoundContainerBase.internals));

            audioClips = internals.FindPropertyRelative(nameof(SoundContainerBase.internals.audioClips));

            data = internals.FindPropertyRelative(nameof(SoundContainerBase.internals.data));

            // Sonity Steam Audio
#if SONITY_ENABLE_INTEGRATION_STEAM_AUDIO
            steamAudio = data.FindPropertyRelative(nameof(SoundContainerInternalsData.steamAudio));
            steamAudioExpand = steamAudio.FindPropertyRelative(nameof(SoundContainerInternalsDataSteamAudio.steamAudioExpand));

            // For this part of the script copied and modified from Steam Audio there is a different license:
            // Copyright 2017-2023 Valve Corporation.
            // Licensed under the Apache License, Version 2.0 (the "License");
            // you may not use this file except in compliance with the License.
            // You may obtain a copy of the License at
            // http://www.apache.org/licenses/LICENSE-2.0
            // Unless required by applicable law or agreed to in writing, software
            // distributed under the License is distributed on an "AS IS" BASIS,
            // WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
            // See the License for the specific language governing permissions and
            // limitations under the License.

            // Copied from SteamAudioSourceInspector
            // Replaced "serializedObject.FindProperty" with "steamAudio.FindPropertyRelative"
            mDirectBinaural = steamAudio.FindPropertyRelative("directBinaural");
            mInterpolation = steamAudio.FindPropertyRelative("interpolation");
            mPerspectiveCorrection = steamAudio.FindPropertyRelative("perspectiveCorrection");
            mDistanceAttenuation = steamAudio.FindPropertyRelative("distanceAttenuation");
            mDistanceAttenuationInput = steamAudio.FindPropertyRelative("distanceAttenuationInput");
            mAirAbsorption = steamAudio.FindPropertyRelative("airAbsorption");
            mAirAbsorptionInput = steamAudio.FindPropertyRelative("airAbsorptionInput");
            mAirAbsorptionLow = steamAudio.FindPropertyRelative("airAbsorptionLow");
            mAirAbsorptionMid = steamAudio.FindPropertyRelative("airAbsorptionMid");
            mAirAbsorptionHigh = steamAudio.FindPropertyRelative("airAbsorptionHigh");
            mDirectivity = steamAudio.FindPropertyRelative("directivity");
            mDirectivityInput = steamAudio.FindPropertyRelative("directivityInput");
            mDipoleWeight = steamAudio.FindPropertyRelative("dipoleWeight");
            mDipolePower = steamAudio.FindPropertyRelative("dipolePower");
            mDirectivityValue = steamAudio.FindPropertyRelative("directivityValue");
            mOcclusion = steamAudio.FindPropertyRelative("occlusion");
            mOcclusionInput = steamAudio.FindPropertyRelative("occlusionInput");
            mOcclusionType = steamAudio.FindPropertyRelative("occlusionType");
            mOcclusionRadius = steamAudio.FindPropertyRelative("occlusionRadius");
            mOcclusionSamples = steamAudio.FindPropertyRelative("occlusionSamples");
            mOcclusionValue = steamAudio.FindPropertyRelative("occlusionValue");
            mTransmission = steamAudio.FindPropertyRelative("transmission");
            mTransmissionType = steamAudio.FindPropertyRelative("transmissionType");
            mTransmissionInput = steamAudio.FindPropertyRelative("transmissionInput");
            mTransmissionLow = steamAudio.FindPropertyRelative("transmissionLow");
            mTransmissionMid = steamAudio.FindPropertyRelative("transmissionMid");
            mTransmissionHigh = steamAudio.FindPropertyRelative("transmissionHigh");
            mTransmissionRays = steamAudio.FindPropertyRelative("maxTransmissionSurfaces");
            mDirectMixLevel = steamAudio.FindPropertyRelative("directMixLevel");
            mReflections = steamAudio.FindPropertyRelative("reflections");
            mReflectionsType = steamAudio.FindPropertyRelative("reflectionsType");
            mUseDistanceCurveForReflections = steamAudio.FindPropertyRelative("useDistanceCurveForReflections");
            mCurrentBakedSource = steamAudio.FindPropertyRelative("currentBakedSource");
            mApplyHRTFToReflections = steamAudio.FindPropertyRelative("applyHRTFToReflections");
            mReflectionsMixLevel = steamAudio.FindPropertyRelative("reflectionsMixLevel");
            mPathing = steamAudio.FindPropertyRelative("pathing");
            mPathingProbeBatch = steamAudio.FindPropertyRelative("pathingProbeBatch");
            mPathValidation = steamAudio.FindPropertyRelative("pathValidation");
            mFindAlternatePaths = steamAudio.FindPropertyRelative("findAlternatePaths");
            mApplyHRTFToPathing = steamAudio.FindPropertyRelative("applyHRTFToPathing");
            mPathingMixLevel = steamAudio.FindPropertyRelative("pathingMixLevel");
            // End of Steam Audio license, resuming original Sonigon copyright
#endif
            notes = internals.FindPropertyRelative(nameof(SoundContainerBase.internals.notes));

            foundReferences = data.FindPropertyRelative(nameof(SoundContainerInternalsData.foundReferences));

            previewAudioMixerGroup = data.FindPropertyRelative(nameof(SoundContainerInternalsData.previewAudioMixerGroup));

            expandPreview = data.FindPropertyRelative(nameof(SoundContainerInternalsData.expandPreview));
            expandAudioClips = data.FindPropertyRelative(nameof(SoundContainerInternalsData.expandAudioClips));
            settingsExpandBase = data.FindPropertyRelative(nameof(SoundContainerInternalsData.settingsExpandBase)); ;
            settingsExpandAdvanced = data.FindPropertyRelative(nameof(SoundContainerInternalsData.settingsExpandAdvanced)); 

            previewCurves = data.FindPropertyRelative(nameof(SoundContainerInternalsData.previewCurves));

            neverStealVoice = data.FindPropertyRelative(nameof(SoundContainerInternalsData.neverStealVoice));
            neverStealVoiceEffects = data.FindPropertyRelative(nameof(SoundContainerInternalsData.neverStealVoiceEffects));

            startPosition = data.FindPropertyRelative(nameof(SoundContainerInternalsData.startPosition));

            reverse = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverse));

            dopplerAmount = data.FindPropertyRelative(nameof(SoundContainerInternalsData.dopplerAmount));

            reverbZoneMixExpand = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixExpand));
            reverbZoneMixDecibel = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixDecibel));
            reverbZoneMixRatio = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixRatio));

            reverbZoneMixDistanceIncrease = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixDistanceIncrease));
            reverbZoneMixDistanceRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixDistanceRolloff));
            reverbZoneMixDistanceCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixDistanceCurve));

            reverbZoneMixIntensityEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixIntensityEnable));
            reverbZoneMixIntensityRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixIntensityRolloff));
            reverbZoneMixIntensityAmount = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixIntensityAmount));
            reverbZoneMixIntensityCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.reverbZoneMixIntensityCurve));

            stereoPanExpand = data.FindPropertyRelative(nameof(SoundContainerInternalsData.stereoPanExpand));
            stereoPanOffset = data.FindPropertyRelative(nameof(SoundContainerInternalsData.stereoPanOffset));
            stereoPanAngleUse = data.FindPropertyRelative(nameof(SoundContainerInternalsData.stereoPanAngleUse));
            stereoPanAngleAmount = data.FindPropertyRelative(nameof(SoundContainerInternalsData.stereoPanAngleAmount));
            stereoPanAngleRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.stereoPanAngleRolloff));

            bypassReverbZones = data.FindPropertyRelative(nameof(SoundContainerInternalsData.bypassReverbZones));
            bypassVoiceEffects = data.FindPropertyRelative(nameof(SoundContainerInternalsData.bypassVoiceEffects));
            bypassListenerEffects = data.FindPropertyRelative(nameof(SoundContainerInternalsData.bypassListenerEffects));

            hrtfPluginSpatialize = data.FindPropertyRelative(nameof(SoundContainerInternalsData.hrtfPluginSpatialize));
            hrtfPluginSpatializePostEffects = data.FindPropertyRelative(nameof(SoundContainerInternalsData.hrtfPluginSpatializePostEffects));

            preventEndClicks = data.FindPropertyRelative(nameof(SoundContainerInternalsData.preventEndClicks));
            loopEnabled = data.FindPropertyRelative(nameof(SoundContainerInternalsData.loopEnabled));
            followPosition = data.FindPropertyRelative(nameof(SoundContainerInternalsData.followPosition));
            stopIfTransformIsNull = data.FindPropertyRelative(nameof(SoundContainerInternalsData.stopIfTransformIsNull));
            //virtualize = data.FindPropertyRelative(nameof(SoundContainerInternalsData.virtualize)); // Virtualize Todo
            randomStartPosition = data.FindPropertyRelative(nameof(SoundContainerInternalsData.randomStartPosition));
            randomStartPositionMin = data.FindPropertyRelative(nameof(SoundContainerInternalsData.randomStartPositionMin));
            randomStartPositionMax = data.FindPropertyRelative(nameof(SoundContainerInternalsData.randomStartPositionMax));
            priority = data.FindPropertyRelative(nameof(SoundContainerInternalsData.priority));
            lockAxisEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lockAxisEnable));
            lockAxis = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lockAxis));
            lockAxisPosition = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lockAxisPosition));
            playOrder = data.FindPropertyRelative(nameof(SoundContainerInternalsData.playOrder));

            distanceEnabled = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distanceEnabled));
            distanceScale = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distanceScale));

            volumeExpand = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeExpand));
            volumeDecibel = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDecibel));
            volumeRatio = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeRatio));

            volumeRandomEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeRandomEnable));
            volumeRandomRangeDecibel = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeRandomRangeDecibel));

            volumeDistanceRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDistanceRolloff));
            volumeDistanceCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDistanceCurve));

            volumeIntensityEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityEnable));
            volumeIntensityRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityRolloff));
            volumeIntensityStrength = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityStrength));
            volumeIntensityCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityCurve));

            volumeDistanceCrossfadeEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDistanceCrossfadeEnable));
            volumeDistanceCrossfadeTotalLayersOneBased = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDistanceCrossfadeTotalLayersOneBased));
            volumeDistanceCrossfadeLayerOneBased = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDistanceCrossfadeLayerOneBased));
            volumeDistanceCrossfadeTotalLayers = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDistanceCrossfadeTotalLayers));
            volumeDistanceCrossfadeLayer = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDistanceCrossfadeLayer));
            volumeDistanceCrossfadeRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDistanceCrossfadeRolloff));
            volumeDistanceCrossfadeCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeDistanceCrossfadeCurve));

            volumeIntensityCrossfadeEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityCrossfadeEnable));
            volumeIntensityCrossfadeTotalLayersOneBased = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityCrossfadeTotalLayersOneBased));
            volumeIntensityCrossfadeLayerOneBased = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityCrossfadeLayerOneBased));
            volumeIntensityCrossfadeTotalLayers = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityCrossfadeTotalLayers));
            volumeIntensityCrossfadeLayer = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityCrossfadeLayer));
            volumeIntensityCrossfadeRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityCrossfadeRolloff));
            volumeIntensityCrossfadeCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.volumeIntensityCrossfadeCurve));

            spatialBlendExpand = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialBlendExpand));
            spatialBlend = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialBlend));

            spatialBlendDistanceRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialBlendDistanceRolloff));
            spatialBlendDistance3DIncrease = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialBlendDistance3DIncrease));
            spatialBlendDistanceCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialBlendDistanceCurve));

            spatialBlendIntensityEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialBlendIntensityEnable));
            spatialBlendIntensityRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialBlendIntensityRolloff));
            spatialBlendIntensityStrength = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialBlendIntensityStrength));
            spatialBlendIntensityCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialBlendIntensityCurve));

            spatialSpreadExpand = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialSpreadExpand));
            spatialSpreadDegrees = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialSpreadDegrees));
            spatialSpreadRatio = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialSpreadRatio));

            spatialSpreadDistanceRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialSpreadDistanceRolloff));
            spatialSpreadDistanceCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialSpreadDistanceCurve));

            spatialSpreadIntensityEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialSpreadIntensityEnable));
            spatialSpreadIntensityRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialSpreadIntensityRolloff));
            spatialSpreadIntensityStrength = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialSpreadIntensityStrength));
            spatialSpreadIntensityCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.spatialSpreadIntensityCurve));

            distortionExpand = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionExpand));
            distortionEnabled = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionEnabled));
            distortionAmount = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionAmount));

            distortionDistanceEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionDistanceEnable));
            distortionDistanceRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionDistanceRolloff));
            distortionDistanceCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionDistanceCurve));

            distortionIntensityEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionIntensityEnable));
            distortionIntensityRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionIntensityRolloff));
            distortionIntensityStrength = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionIntensityStrength));
            distortionIntensityCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.distortionIntensityCurve));

            lowpassExpand = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassExpand));
            lowpassEnabled = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassEnabled));
            lowpassFrequencyEditor = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassFrequencyEditor));
            lowpassFrequencyEngine = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassFrequencyEngine));
            lowpassAmountEditor = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassAmountEditor));
            lowpassAmountEngine = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassAmountEngine));

            lowpassDistanceEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassDistanceEnable));
            lowpassDistanceFrequencyRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassDistanceFrequencyRolloff));
            lowpassDistanceFrequencyCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassDistanceFrequencyCurve));
            lowpassDistanceAmountRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassDistanceAmountRolloff));
            lowpassDistanceAmountCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassDistanceAmountCurve));

            lowpassIntensityEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassIntensityEnable));
            lowpassIntensityFrequencyRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassIntensityFrequencyRolloff));
            lowpassIntensityFrequencyStrength = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassIntensityFrequencyStrength));
            lowpassIntensityFrequencyCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassIntensityFrequencyCurve));
            lowpassIntensityAmountRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassIntensityAmountRolloff));
            lowpassIntensityAmountStrength = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassIntensityAmountStrength));
            lowpassIntensityAmountCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.lowpassIntensityAmountCurve));

            highpassExpand = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassExpand));
            highpassEnabled = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassEnabled));
            highpassFrequencyEditor = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassFrequencyEditor));
            highpassFrequencyEngine = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassFrequencyEngine));
            highpassAmountEditor = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassAmountEditor));
            highpassAmountEngine = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassAmountEngine));

            highpassDistanceEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassDistanceEnable));
            highpassDistanceFrequencyRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassDistanceFrequencyRolloff));
            highpassDistanceFrequencyCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassDistanceFrequencyCurve));
            highpassDistanceAmountRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassDistanceAmountRolloff));
            highpassDistanceAmountCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassDistanceAmountCurve));

            highpassIntensityEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassIntensityEnable));
            highpassIntensityFrequencyRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassIntensityFrequencyRolloff));
            highpassIntensityFrequencyStrength = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassIntensityFrequencyStrength));
            highpassIntensityFrequencyCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassIntensityFrequencyCurve));
            highpassIntensityAmountRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassIntensityAmountRolloff));
            highpassIntensityAmountStrength = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassIntensityAmountStrength));
            highpassIntensityAmountCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.highpassIntensityAmountCurve));

            pitchExpand = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchExpand));
            pitchSemitoneEditor = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchSemitone));
            pitchRatio = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchRatio));
            pitchRandomEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchRandomEnable));
            pitchRandomRangeSemitone = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchRandomRangeSemitoneBipolar));

            pitchIntensityEnable = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityEnable));
            pitchIntensityLowSemitone = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityLowSemitone));
            pitchIntensityLowRatio = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityLowRatio));
            pitchIntensityHighSemitone = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityHighSemitone));
            pitchIntensityHighRatio = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityHighRatio));
            pitchIntensityRangeSemitone = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityRangeSemitone));
            pitchIntensityRangeRatio = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityRangeRatio));
            pitchIntensityBaseSemitone = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityBaseSemitone));
            pitchIntensityBaseRatio = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityBaseRatio));
            pitchIntensityRolloff = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityRolloff));
            pitchIntensityCurve = data.FindPropertyRelative(nameof(SoundContainerInternalsData.pitchIntensityCurve));
        }

        public void BeginChange() {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
        }

        public void EndChange() {
            if (EditorGUI.EndChangeCheck()) {
                serializedObject.ApplyModifiedProperties();
            }
        }

        private GUIStyle guiStyleBoldCenter = new GUIStyle();
        private Color defaultGuiColor;

        // To get rid of errors when multi-selecting and moving items up/down, creating a duplicate
        private bool verticalBegun = false;

        public void StartBackgroundColor(Color color) {
            GUI.color = color;
            EditorGUILayout.BeginVertical("Button");
            GUI.color = defaultGuiColor;
            verticalBegun = true;
        }

        public void StopBackgroundColor() {
            if (verticalBegun) {
                EditorGUILayout.EndVertical();
            }
            verticalBegun = false;
        }

        public void InitializeEditors() {
            curveDraw = ScriptableObject.CreateInstance<SoundContainerEditorCurveDraw>();
            curveDraw.Initialize(this);
            preview = ScriptableObject.CreateInstance<SoundContainerEditorPreview>();
            preview.Initialize(this);
            updateAudioClips = ScriptableObject.CreateInstance<SoundContainerEditorFindAssets>();
            updateAudioClips.Initialize(this);
        }

        public override void OnInspectorGUI() {

            mTarget = (SoundContainerBase)target;

            mTargets = new SoundContainerBase[targets.Length];
            for (int i = 0; i < targets.Length; i++) {
                mTargets[i] = (SoundContainerBase)targets[i];
            }

            if (!initialized) {
                initialized = true;
                FindProperties();
                InitializeEditors();
            }

            defaultGuiColor = GUI.color;

            guiStyleBoldCenter.fontSize = 16;
            guiStyleBoldCenter.fontStyle = FontStyle.Bold;
            guiStyleBoldCenter.alignment = TextAnchor.MiddleCenter;
            if (EditorGUIUtility.isProSkin) {
                guiStyleBoldCenter.normal.textColor = EditorColorProSkin.GetDarkSkinTextColor();
            }

            EditorGUI.indentLevel = 0;

            EditorGuiFunction.DrawObjectNameBox((UnityEngine.Object)mTarget, NameOf.SoundContainer, EditorTextSoundContainer.soundContainerTooltip, true);
            EditorTrial.InfoText();
            EditorGUILayout.Separator();

            GuiNotes();
            GuiPresetsMenu();

            preview.Preview();
            EditorGUILayout.Separator();

            // If properties are not found, then find properties
            try {
                if (loopEnabled.boolValue == true) {
                }
            } catch {
                FindProperties();
            }

            EditorGUI.indentLevel = 1;

            GuiSpatializationInfo();
            GuiAudioClips();
            GuiSettingsBase();
            GuiVolume();
            GuiPitch();
            GuiSpatialBlend();
            GuiSpatialSpread();
            GuiStereoPan();
            GuiReverbZoneMix();
            GuiDistortion();
            GuiLowpass();
            GuiHighpass();
            GuiReset();
            GuiFindReferences();

            // Asset GUID
            // Transparent background so the offset will be right
            StartBackgroundColor(new Color(0f, 0f, 0f, 0f));
            BeginChange();
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.PropertyField(assetGuid, new GUIContent(EditorTextAssetGuid.assetGuidLabel, EditorTextAssetGuid.assetGuidTooltip));
            for (int i = 0; i < mTargets.Length; i++) {
                string assetGuidTemp = EditorAssetGuid.GetAssetGuid(mTargets[i]);
                long assetGuidHashTemp = EditorAssetGuid.GetInt64HashFromString(assetGuidTemp);
                if (mTargets[i].assetGuid != assetGuidTemp || mTargets[i].assetGuidHash != assetGuidHashTemp) {
                    mTargets[i].assetGuid = assetGuidTemp;
                    mTargets[i].assetGuidHash = assetGuidHashTemp;
                    EditorUtility.SetDirty(mTargets[i]);
                }
            }
            EditorGUI.EndDisabledGroup();
            EndChange();
            StopBackgroundColor();
        }

        private void GuiNotes() {
            // Notes
            EditorGUI.indentLevel = 0;
            Color previousColor = GUI.color;
            if (string.IsNullOrEmpty(notes.stringValue) || notes.stringValue == "Notes") {
                // Make less transparent if empty or default text
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
            }
            BeginChange();
            notes.stringValue = EditorGUILayout.TextArea(notes.stringValue);
            EndChange();
            GUI.color = previousColor;
        }

        private void DragAndDropCallback<T>(T[] draggedObjects) where T : UnityEngine.Object {
            AudioClip[] newObjects = draggedObjects as AudioClip[];
            // If there are any objects of the right type dragged
            if (newObjects.Length > 0) {
                for (int i = 0; i < mTargets.Length; i++) {
                    Undo.RecordObject(mTargets[i], $"Drag and Dropped {nameof(AudioClip)}");
                    mTargets[i].internals.audioClips = new AudioClip[newObjects.Length];
                    for (int ii = 0; ii < newObjects.Length; ii++) {
                        mTargets[i].internals.audioClips[ii] = newObjects[ii];
                    }
                    // Expands the audioClip array
                    audioClips.isExpanded = true;
                    EditorUtility.SetDirty(mTargets[i]);
                }
            }
        }

        // Copied from EditorToolSelectSameType
        private static void SelectObjectsOfSameType(bool subFolders) {

            AssetDatabase.SaveAssets();

            if (Selection.objects.Length > 0) {

                UnityEngine.Object selectedObject = Selection.objects[0];

                if (selectedObject == null) {
                    return;
                }

                string selectedPath = AssetDatabase.GUIDToAssetPath(Selection.assetGUIDs[0]);

                // Remove Filename from Path
                selectedPath = selectedPath.Replace(EditorPath.GetFileName(selectedPath), "");

                // Finding all guids of right type
                List<string> foundGuids = AssetDatabase.FindAssets($"t:" + selectedObject.GetType().Name, new[] { selectedPath }).ToList<string>();

                // Removing files in subfolders
                if (!subFolders) {
                    for (int i = foundGuids.Count - 1; i >= 0; i--) {
                        // Getting path
                        string tempString = AssetDatabase.GUIDToAssetPath(foundGuids[i]);
                        // Removing parent path
                        tempString = tempString.Replace(selectedPath, "");
                        // If contains subpath
                        if (tempString.Contains("/")) {
                            // Remove index with subpath
                            foundGuids.RemoveAt(i);
                        }
                    }
                }

                List<UnityEngine.Object> foundObjects = new List<UnityEngine.Object>();

                // Load found objects
                for (int i = 0; i < foundGuids.Count; i++) {
                    UnityEngine.Object tempObject = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(foundGuids[i]), typeof(UnityEngine.Object));
                    if (tempObject != null) {
                        if (!foundObjects.Contains(tempObject)) {
                            foundObjects.Add(tempObject);
                        }
                    }
                }

                // Set selection to found objects
                Selection.objects = foundObjects.ToArray();
                AssetDatabase.SaveAssets();
            }
        }

        private void SelectAudioClipsInSoundContainers(List<SoundContainerBase> soundContainers) {
            AssetDatabase.SaveAssets();
            List<AudioClip> audioClips = new List<AudioClip>();
            for (int i = 0; i < soundContainers.Count; i++) {
                SoundContainerBase soundContainer = soundContainers[i];
                for (int ii = 0; ii < soundContainer.internals.audioClips.Length; ii++) {
                    AudioClip audioClip = soundContainer.internals.audioClips[ii];
                    if (audioClip != null && !audioClips.Contains(audioClip)) {
                        audioClips.Add(audioClip);
                    }
                }
            }
            Selection.objects = audioClips.ToArray();
            AssetDatabase.SaveAssets();
        }

        private void GuiSpatializationInfo() {

            List<SoundContainerBase> sounds3d = new List<SoundContainerBase>();
            List<SoundContainerBase> sounds2d = new List<SoundContainerBase>();
            List<SoundContainerBase> sounds3dNoDistance = new List<SoundContainerBase>();
            List<SoundContainerBase> sounds2dDistance = new List<SoundContainerBase>();

            for (int i = 0; i < mTargets.Length; i++) {
                SoundContainerBase soundContainer = mTargets[i];
                if (soundContainer == null) {
                    continue;
                }
                if (soundContainer.internals.data.spatialBlend > 0f && soundContainer.internals.data.distanceEnabled) {
                    if (!sounds3d.Contains(soundContainer)) {
                        sounds3d.Add(soundContainer);
                    }
                } else if (soundContainer.internals.data.spatialBlend == 0f && !soundContainer.internals.data.distanceEnabled) {
                    if (!sounds2d.Contains(soundContainer)) {
                        sounds2d.Add(soundContainer);
                    }
                } else if (soundContainer.internals.data.spatialBlend > 0f && !soundContainer.internals.data.distanceEnabled) {
                    if (!sounds3dNoDistance.Contains(soundContainer)) {
                        sounds3dNoDistance.Add(soundContainer);
                    }
                } else if (soundContainer.internals.data.spatialBlend == 0f && soundContainer.internals.data.distanceEnabled) {
                    if (!sounds2dDistance.Contains(soundContainer)) {
                        sounds2dDistance.Add(soundContainer);
                    }
                }
            }

            float multipleTypesCounter = 0;
            bool multipleTypesSelected = false;
            if (sounds3d.Count > 0) {
                multipleTypesCounter++;
            }
            if (sounds2d.Count > 0) {
                multipleTypesCounter++;
            }
            if (sounds3dNoDistance.Count > 0) {
                multipleTypesCounter++;
            }
            if (sounds2dDistance.Count > 0) {
                multipleTypesCounter++;
            }
            if (multipleTypesCounter > 1) {
                multipleTypesSelected = true;
            }

            if (multipleTypesCounter == 0) {
                // Then no objects are selected so dont draw anything more
                return;
            }

            float buttonLabelWidthHalf = (EditorGUIUtility.currentViewWidth - EditorGUIUtility.labelWidth) * 0.5f - 26f;
            float buttonLabelWidthThird = (EditorGUIUtility.currentViewWidth - EditorGUIUtility.labelWidth) / 3f - 18f;

            if (multipleTypesSelected) {
                StartBackgroundColor(EditorColor.GetSound2d3dMixedColor(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));
            } else if (sounds3d.Count > 0) {
                StartBackgroundColor(EditorColor.GetSound3dColor(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));
            } else if (sounds2d.Count > 0) {
                StartBackgroundColor(EditorColor.GetSound2dColor(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));
            } else  {
                // For sounds3dNoDistance and sounds2dDistance
                StartBackgroundColor(EditorColor.GetSound2dDistand3dNoDistanceColor(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));
            }
            EditorGUI.indentLevel = 0;

            // 3D
            if (sounds3d.Count > 0) {
                EditorGUILayout.BeginHorizontal();
                if (sounds3d.Count == 1) {
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatializationInfo3DSoundSingleLabel, EditorTextSoundContainer.spatializationInfo3DSoundTooltip), GUILayout.Width(EditorGUIUtility.labelWidth));
                } else {
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatializationInfo3DSoundMultipleLabel + " (" + sounds3d.Count + ")", EditorTextSoundContainer.spatializationInfo3DSoundTooltip), GUILayout.Width(EditorGUIUtility.labelWidth));
                }
                if (multipleTypesSelected) {
                    BeginChange();
                    if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectSCLabel, EditorTextSoundContainer.spatializationInfoSelectSCTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                        AssetDatabase.SaveAssets();
                        List<SoundContainerBase> soundContainers = new List<SoundContainerBase>();
                        for (int i = 0; i < mTargets.Length; i++) {
                            SoundContainerBase target = mTargets[i];
                            if (target.internals.data.spatialBlend > 0f && target.internals.data.distanceEnabled) {
                                soundContainers.Add(target);
                            }
                        }
                        Selection.objects = soundContainers.ToArray();
                        AssetDatabase.SaveAssets();
                    }
                    EndChange();
                } else {
                    BeginChange();
                    if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectAllSCLabel, EditorTextSoundContainer.spatializationInfoSelectAllSCTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                        SelectObjectsOfSameType(false);
                    }
                    EndChange();
                }
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectACLabel, EditorTextSoundContainer.spatializationInfoSelectACTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    SelectAudioClipsInSoundContainers(sounds3d);
                }
                EndChange();
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSet2DLabel, EditorTextSoundContainer.spatializationInfoSet2DTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    for (int i = 0; i < sounds3d.Count; i++) {
                        SoundContainerBase soundContainer = sounds3d[i];
                        Undo.RecordObject(soundContainer, "Set 2D & Disable Distance");
                        soundContainer.internals.data.spatialBlend = 0f;
                        soundContainer.internals.data.distanceEnabled = false;
                        EditorUtility.SetDirty(soundContainer);
                    }
                }
                EndChange();
                EditorGUILayout.EndHorizontal();
            }

            // 2D
            if (sounds2d.Count > 0) {
                EditorGUILayout.BeginHorizontal();
                if (sounds2d.Count == 1) {
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatializationInfo2DSoundSingleLabel, EditorTextSoundContainer.spatializationInfo2DSoundTooltip), GUILayout.Width(EditorGUIUtility.labelWidth));
                } else {
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatializationInfo2DSoundMultipleLabel + " (" + sounds2d.Count + ")", EditorTextSoundContainer.spatializationInfo2DSoundTooltip), GUILayout.Width(EditorGUIUtility.labelWidth));
                }
                if (multipleTypesSelected) {
                    BeginChange();
                    if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectSCLabel, EditorTextSoundContainer.spatializationInfoSelectSCTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                        AssetDatabase.SaveAssets();
                        List<SoundContainerBase> soundContainers = new List<SoundContainerBase>();
                        for (int i = 0; i < mTargets.Length; i++) {
                            SoundContainerBase target = mTargets[i];
                            if (target.internals.data.spatialBlend == 0f && !target.internals.data.distanceEnabled) {
                                soundContainers.Add(target);
                            }
                        }
                        Selection.objects = soundContainers.ToArray();
                        AssetDatabase.SaveAssets();
                    }
                    EndChange();
                } else {
                    BeginChange();
                    if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectAllSCLabel, EditorTextSoundContainer.spatializationInfoSelectAllSCTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                        SelectObjectsOfSameType(false);
                    }
                    EndChange();
                }
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectACLabel, EditorTextSoundContainer.spatializationInfoSelectACTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    SelectAudioClipsInSoundContainers(sounds2d);
                }
                EndChange();
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSet3DLabel, EditorTextSoundContainer.spatializationInfoSet3DTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    for (int i = 0; i < sounds2d.Count; i++) {
                        SoundContainerBase soundContainer = sounds2d[i];
                        Undo.RecordObject(soundContainer, "Set 3D & Enable Distance");
                        soundContainer.internals.data.spatialBlend = 1f;
                        soundContainer.internals.data.distanceEnabled = true;
                        EditorUtility.SetDirty(soundContainer);
                    }
                }
                EndChange();
                EditorGUILayout.EndHorizontal();
            }

            // 3D without Distance
            if (sounds3dNoDistance.Count > 0) {
                EditorGUILayout.BeginHorizontal();
                if (sounds3dNoDistance.Count == 1) {
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatializationInfo3DSoundWithoutDistanceSingleLabel, EditorTextSoundContainer.spatializationInfo3DSoundWithoutDistanceTooltip), GUILayout.Width(EditorGUIUtility.labelWidth));
                } else {
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatializationInfo3DSoundWithoutDistanceMultipleLabel + " (" + sounds3dNoDistance.Count + ")", EditorTextSoundContainer.spatializationInfo3DSoundWithoutDistanceTooltip), GUILayout.Width(EditorGUIUtility.labelWidth));
                }
                if (multipleTypesSelected) {
                    BeginChange();
                    if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectSCLabel, EditorTextSoundContainer.spatializationInfoSelectSCTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                        AssetDatabase.SaveAssets();
                        List<SoundContainerBase> soundContainers = new List<SoundContainerBase>();
                        for (int i = 0; i < mTargets.Length; i++) {
                            SoundContainerBase target = mTargets[i];
                            if (target.internals.data.spatialBlend > 0f && !target.internals.data.distanceEnabled) {
                                soundContainers.Add(target);
                            }
                        }
                        Selection.objects = soundContainers.ToArray();
                        AssetDatabase.SaveAssets();
                    }
                    EndChange();
                } else {
                    BeginChange();
                    if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectAllSCLabel, EditorTextSoundContainer.spatializationInfoSelectAllSCTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                        SelectObjectsOfSameType(false);
                    }
                    EndChange();
                }
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectACLabel, EditorTextSoundContainer.spatializationInfoSelectACTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    SelectAudioClipsInSoundContainers(sounds3dNoDistance);
                }
                EndChange();
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSet2DLabel, EditorTextSoundContainer.spatializationInfoSet2DTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    for (int i = 0; i < sounds3dNoDistance.Count; i++) {
                        SoundContainerBase soundContainer = sounds3dNoDistance[i];
                        Undo.RecordObject(soundContainer, "Set 2D & Disable Distance");
                        soundContainer.internals.data.spatialBlend = 0f;
                        soundContainer.internals.data.distanceEnabled = false;
                        EditorUtility.SetDirty(soundContainer);
                    }
                }
                EndChange();
                EditorGUILayout.EndHorizontal();
            }

            // 2D with Distance
            if (sounds2dDistance.Count > 0) {
                EditorGUILayout.BeginHorizontal();
                if (sounds2dDistance.Count == 1) {
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatializationInfo2DSoundWithDistanceSingleLabel, EditorTextSoundContainer.spatializationInfo2DSoundWithDistanceTooltip), GUILayout.Width(EditorGUIUtility.labelWidth));
                } else {
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatializationInfo2DSoundWithDistanceMultipleLabel + " (" + sounds2dDistance.Count + ")", EditorTextSoundContainer.spatializationInfo2DSoundWithDistanceTooltip), GUILayout.Width(EditorGUIUtility.labelWidth));
                }
                if (multipleTypesSelected) {
                    BeginChange();
                    if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectSCLabel, EditorTextSoundContainer.spatializationInfoSelectSCTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                        AssetDatabase.SaveAssets();
                        List<SoundContainerBase> soundContainers = new List<SoundContainerBase>();
                        for (int i = 0; i < mTargets.Length; i++) {
                            SoundContainerBase target = mTargets[i];
                            if (target.internals.data.spatialBlend == 0f && target.internals.data.distanceEnabled) {
                                soundContainers.Add(target);
                            }
                        }
                        Selection.objects = soundContainers.ToArray();
                        AssetDatabase.SaveAssets();
                    }
                    EndChange();
                } else {
                    BeginChange();
                    if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectAllSCLabel, EditorTextSoundContainer.spatializationInfoSelectAllSCTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                        SelectObjectsOfSameType(false);
                    }
                    EndChange();
                }
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSelectACLabel, EditorTextSoundContainer.spatializationInfoSelectACTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    SelectAudioClipsInSoundContainers(sounds2dDistance);
                }
                EndChange();
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoSet3DLabel, EditorTextSoundContainer.spatializationInfoSet3DTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    for (int i = 0; i < sounds2dDistance.Count; i++) {
                        SoundContainerBase soundContainer = sounds2dDistance[i];
                        Undo.RecordObject(soundContainer, "Set 3D & Enable Distance");
                        soundContainer.internals.data.spatialBlend = 1f;
                        soundContainer.internals.data.distanceEnabled = true;
                        EditorUtility.SetDirty(soundContainer);
                    }
                }
                EndChange();
                EditorGUILayout.EndHorizontal();
            }

            // Edit All
            if (multipleTypesSelected) {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatializationInfoEditAllLabel, EditorTextSoundContainer.spatializationInfoEditAllTooltip), GUILayout.Width(EditorGUIUtility.labelWidth));
                BeginChange();
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoAllSelectACLabel, EditorTextSoundContainer.spatializationInfoAllSelectACTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    List<SoundContainerBase> soundContainers = new List<SoundContainerBase>();
                    soundContainers.AddRange(sounds3d);
                    soundContainers.AddRange(sounds2d);
                    soundContainers.AddRange(sounds3dNoDistance);
                    soundContainers.AddRange(sounds2dDistance);
                    SelectAudioClipsInSoundContainers(soundContainers);
                }
                EndChange();
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoAllSet3DLabel, EditorTextSoundContainer.spatializationInfoAllSet3DTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    for (int i = 0; i < mTargets.Length; i++) {
                        SoundContainerBase target = mTargets[i];
                        Undo.RecordObject(target, "Set 3D & Enable Distance");
                        target.internals.data.spatialBlend = 1f;
                        target.internals.data.distanceEnabled = true;
                        EditorUtility.SetDirty(target);
                    }
                }
                EndChange();
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.spatializationInfoAllSet2DLabel, EditorTextSoundContainer.spatializationInfoAllSet2DTooltip), GUILayout.Width(buttonLabelWidthThird))) {
                    for (int i = 0; i < mTargets.Length; i++) {
                        SoundContainerBase target = mTargets[i];
                        Undo.RecordObject(target, "Set 2D & Disable Distance");
                        target.internals.data.spatialBlend = 0f;
                        target.internals.data.distanceEnabled = false;
                        EditorUtility.SetDirty(target);
                    }
                }
                EndChange();
                EditorGUILayout.EndHorizontal();
            }
            StopBackgroundColor();

            EditorGUILayout.Separator();
        }

        private void GuiAudioClips() {

            EditorGUI.indentLevel = 1;
            StartBackgroundColor(EditorColor.GetSettings(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            BeginChange();
            EditorGuiFunction.DrawFoldout(expandAudioClips, "AudioClips");
            EndChange();

            if (expandAudioClips.boolValue) {

                // Menu for updating AudioClips
                EditorGUILayout.BeginHorizontal();
                // For offsetting the buttons to the right
                EditorGUILayout.LabelField(new GUIContent(""), GUILayout.Width(EditorGUIUtility.labelWidth));
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.updateAudioClipsLabel, EditorTextSoundContainer.updateAudioClipsTooltip))) {
                    updateAudioClips.MenuFindAsset();
                }
                EndChange();
                EditorGUILayout.EndHorizontal();

                // Audio Clip
                int lowestArrayLength = int.MaxValue;
                for (int n = 0; n < mTargets.Length; n++) {
                    if (lowestArrayLength > mTargets[n].internals.audioClips.Length) {
                        lowestArrayLength = mTargets[n].internals.audioClips.Length;
                    }
                }
                EditorGUI.indentLevel = 1;
                EditorGuiFunction.DrawReordableArray(audioClips, serializedObject, lowestArrayLength, false);

                EditorDragAndDropArea.DrawDragAndDropAreaCustomEditor<AudioClip>(new EditorDragAndDropArea.DragAndDropAreaInfo($"{nameof(AudioClip)}"), DragAndDropCallback);
            }

            if (ShouldDebug.GuiWarnings()) {
                // Waring if null/empty AudioClips
                if (mTarget.internals.audioClips.Length == 0) {
                    EditorGUILayout.Separator();
                    EditorGUILayout.HelpBox(EditorTextSoundContainer.audioClipWarningEmpty, MessageType.Warning);
                    EditorGUILayout.Separator();
                } else {
                    bool audioClipsNull = false;
                    for (int i = 0; i < mTarget.internals.audioClips.Length; i++) {
                        if (mTarget.internals.audioClips[i] == null) {
                            audioClipsNull = true;
                            break;
                        }
                    }
                    if (audioClipsNull) {
                        EditorGUILayout.Separator();
                        EditorGUILayout.HelpBox(EditorTextSoundContainer.audioClipWarningNull, MessageType.Warning);
                        EditorGUILayout.Separator();
                    }
                }
            }
            StopBackgroundColor();
            EditorGUILayout.Separator();
        }
        
        private void GuiPresetsMenu() {
            // Transparent background so the offset will be right
            StartBackgroundColor(new Color(0f, 0f, 0f, 0f));
            EditorGUILayout.BeginHorizontal();
            // For offsetting the buttons to the right
            EditorGUILayout.LabelField(new GUIContent(""), GUILayout.Width(EditorGUIUtility.labelWidth));
            BeginChange();
            if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.presetsLabel, EditorTextSoundContainer.presetsTooltip))) {
                PresetsMenuDraw();
            }
            EndChange();
            EditorGUILayout.EndHorizontal();
            StopBackgroundColor();
        }

        private enum PresetType {
            SFX3D,
            SFX2D,
            UI,
            Music,
            Looping,
            Crossfades,
            PresetSame,
            PresetMatchName,
        }

        private class PresetsMenuObject {
            public PresetType presetType;
            public SoundContainerBase soundContainerPreset;

            public PresetsMenuObject(PresetType presetType, SoundContainerBase soundContainerPreset = null) {
                this.presetType = presetType;
                this.soundContainerPreset = soundContainerPreset;
            }
        }

        private void PresetsMenuDraw() {
            GenericMenu menu = new GenericMenu();

            // Tooltips dont work for menu
            menu.AddItem(new GUIContent("Apply SFX 3D Settings"), false, PresetsMenuCallback, new PresetsMenuObject(PresetType.SFX3D));
            menu.AddItem(new GUIContent("Apply SFX 2D Settings"), false, PresetsMenuCallback, new PresetsMenuObject(PresetType.SFX2D));
            menu.AddItem(new GUIContent("Apply UI Settings"), false, PresetsMenuCallback, new PresetsMenuObject(PresetType.UI));
            menu.AddItem(new GUIContent("Apply Music Settings"), false, PresetsMenuCallback, new PresetsMenuObject(PresetType.Music));
            menu.AddItem(new GUIContent("Automatic Looping"), false, PresetsMenuCallback, new PresetsMenuObject(PresetType.Looping));
            menu.AddItem(new GUIContent("Automatic Crossfades"), false, PresetsMenuCallback, new PresetsMenuObject(PresetType.Crossfades));

            menu.AddSeparator("");

            bool anyMenuItemAdded = false;

            SoundPresetFind.FindAllSoundPresets();
            if (SoundPresetFind.soundPresets != null && SoundPresetFind.soundPresets.Length > 0) {
                for (int i = 0; i < SoundPresetFind.soundPresets.Length; i++) {
                    SoundPresetBase soundPreset = SoundPresetFind.soundPresets[i];
                    if (soundPreset != null && !soundPreset.internals.disableAll) {
                        for (int ii = 0; ii < SoundPresetFind.soundPresets[i].internals.soundPresetGroup.Length; ii++) {
                            SoundPresetGroup soundPresetGroup = SoundPresetFind.soundPresets[i].internals.soundPresetGroup[ii];
                            if (soundPresetGroup != null && !soundPresetGroup.disable && soundPresetGroup.soundContainerPreset != null) {
                                anyMenuItemAdded = true;
                                string parentName = "Preset - " + soundPresetGroup.soundContainerPreset.name;
                                menu.AddItem(new GUIContent(parentName), false, PresetsMenuCallback, new PresetsMenuObject(PresetType.PresetSame, soundPresetGroup.soundContainerPreset));
                            }
                        }
                    }
                }
                if (anyMenuItemAdded) {
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Preset - Auto Match"), false, PresetsMenuCallback, new PresetsMenuObject(PresetType.PresetMatchName));
                } else {
                    menu.AddItem(new GUIContent($"All {NameOf.SoundPreset}s are disabled or empty"), false, PresetsMenuCallback, null);
                }
            } else {
                menu.AddItem(new GUIContent($"Make custom presets using the {NameOf.SoundPreset} object"), false, PresetsMenuCallback, null);
            }

            menu.ShowAsContext();
        }

        private void PresetsMenuCallback(object obj) {
            try {
                PresetsMenuObject menuObject = (PresetsMenuObject)obj;
                if (menuObject == null) {
                    return;
                }
                // If updating this, update the tooltip and documentation also
                if (menuObject.presetType == PresetType.SFX3D) {
                    for (int i = 0; i < mTargets.Length; i++) {
                        Undo.RecordObject(mTargets[i], "Set to SFX 3D");
                        mTargets[i].internals.data.distanceEnabled = true;
                        mTargets[i].internals.data.spatialBlend = 1f;
                        mTargets[i].internals.data.neverStealVoice = false;
                        mTargets[i].internals.data.neverStealVoiceEffects = false;
                        mTargets[i].internals.data.pitchRandomEnable = true;
                        mTargets[i].internals.data.priority = 0.5f;
                        mTargets[i].internals.data.reverbZoneMixDecibel = 0f;
                        mTargets[i].internals.data.reverbZoneMixRatio = 1f;
                        EditorUtility.SetDirty(mTargets[i]);
                    }
                }
                else if (menuObject.presetType == PresetType.SFX2D) {
                    for (int i = 0; i < mTargets.Length; i++) {
                        Undo.RecordObject(mTargets[i], "Set to SFX 2D");
                        mTargets[i].internals.data.distanceEnabled = false;
                        mTargets[i].internals.data.spatialBlend = 0f;
                        mTargets[i].internals.data.neverStealVoice = false;
                        mTargets[i].internals.data.neverStealVoiceEffects = false;
                        mTargets[i].internals.data.pitchRandomEnable = true;
                        mTargets[i].internals.data.priority = 0.5f;
                        mTargets[i].internals.data.reverbZoneMixDecibel = 0f;
                        mTargets[i].internals.data.reverbZoneMixRatio = 1f;
                        EditorUtility.SetDirty(mTargets[i]);
                    }
                }
                else if (menuObject.presetType == PresetType.UI) {
                    for (int i = 0; i < mTargets.Length; i++) {
                        Undo.RecordObject(mTargets[i], "Set to UI");
                        mTargets[i].internals.data.distanceEnabled = false;
                        mTargets[i].internals.data.spatialBlend = 0f;
                        mTargets[i].internals.data.neverStealVoice = false;
                        mTargets[i].internals.data.neverStealVoiceEffects = false;
                        mTargets[i].internals.data.pitchRandomEnable = false;
                        mTargets[i].internals.data.priority = 0.5f;
                        mTargets[i].internals.data.reverbZoneMixDecibel = Mathf.NegativeInfinity;
                        mTargets[i].internals.data.reverbZoneMixRatio = 0f;
                        EditorUtility.SetDirty(mTargets[i]);
                    }
                }
                else if (menuObject.presetType == PresetType.Music) {
                    for (int i = 0; i < mTargets.Length; i++) {
                        Undo.RecordObject(mTargets[i], "Set to Music Settings");
                        mTargets[i].internals.data.distanceEnabled = false;
                        mTargets[i].internals.data.spatialBlend = 0f;
                        mTargets[i].internals.data.neverStealVoice = true;
                        mTargets[i].internals.data.neverStealVoiceEffects = true;
                        mTargets[i].internals.data.pitchRandomEnable = false;
                        mTargets[i].internals.data.volumeRandomEnable = false;
                        mTargets[i].internals.data.reverbZoneMixDecibel = Mathf.NegativeInfinity;
                        mTargets[i].internals.data.reverbZoneMixRatio = 0f;
                        mTargets[i].internals.data.priority = 1;
                        EditorUtility.SetDirty(mTargets[i]);
                    }
                } 
                else if (menuObject.presetType == PresetType.Looping) {
                    for (int i = 0; i < mTargets.Length; i++) {
                        Undo.RecordObject(mTargets[i], "Automatic Looping");
                        if (mTargets[i].name.ToLowerInvariant().Contains("loop")) {
                            mTargets[i].internals.data.loopEnabled = true;
                            mTargets[i].internals.data.followPosition = true;
                            mTargets[i].internals.data.randomStartPosition = true;
                            mTargets[i].internals.data.stopIfTransformIsNull = true;
                            //mTargets[i].internals.data.virtualize = true; // Virtualize Todo
                        }
                        EditorUtility.SetDirty(mTargets[i]);
                    }
                } 
                else if (menuObject.presetType == PresetType.Crossfades) {

                    if (mTargets.Length <= 1) {
                        return;
                    }

                    List<CrossfadeGroup> crossfadeGroups = new List<CrossfadeGroup>();

                    for (int i = 0; i < targets.Length; i++) {
                        string tempName = mTargets[i].name;
                        CrossfadePartType tempCrossfadeType = NameIsCrossfade(ref tempName);
                        if (tempCrossfadeType != CrossfadePartType.None) {
                            CrossfadePart newPart = new CrossfadePart();
                            newPart.crossfadePartType = tempCrossfadeType;
                            newPart.soundContainer = mTargets[i];

                            bool found = false;
                            for (int ii = 0; ii < crossfadeGroups.Count; ii++) {
                                if (crossfadeGroups[ii].name == tempName) {
                                    found = true;
                                    crossfadeGroups[ii].crossfadeParts.Add(newPart);
                                }
                            }
                            if (!found) {
                                CrossfadeGroup newGroup = new CrossfadeGroup();
                                newGroup.name = tempName;
                                newGroup.crossfadeParts.Add(newPart);
                                crossfadeGroups.Add(newGroup);
                            }
                        }
                    }

                    for (int i = 0; i < crossfadeGroups.Count; i++) {
                        bool containsDistanceClose = false;
                        bool containsDistanceDistant = false;
                        bool containsDistanceFar = false;
                        bool containsIntensitySoft = false;
                        bool containsIntensityMedium = false;
                        bool containsIntensityHard = false;

                        for (int ii = 0; ii < crossfadeGroups[i].crossfadeParts.Count; ii++) {
                            switch (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType) {
                                case CrossfadePartType.Close:
                                    containsDistanceClose = true;
                                    break;
                                case CrossfadePartType.Distant:
                                    containsDistanceDistant = true;
                                    break;
                                case CrossfadePartType.Far:
                                    containsDistanceFar = true;
                                    break;
                                case CrossfadePartType.Soft:
                                    containsIntensitySoft = true;
                                    break;
                                case CrossfadePartType.Medium:
                                    containsIntensityMedium = true;
                                    break;
                                case CrossfadePartType.Hard:
                                    containsIntensityHard = true;
                                    break;
                            }
                        }
                        for (int ii = 0; ii < crossfadeGroups[i].crossfadeParts.Count; ii++) {
                            if (containsDistanceClose && containsDistanceDistant && containsDistanceFar) {
                                if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Close) {
                                    SetCrossfadeDistance(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 3, 1);
                                } else if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Distant) {
                                    SetCrossfadeDistance(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 3, 2);
                                } else if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Far) {
                                    SetCrossfadeDistance(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 3, 3);
                                }
                            }
                            else if (containsDistanceClose && containsDistanceDistant) {
                                if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Close) {
                                    SetCrossfadeDistance(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 2, 1);
                                } else if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Distant) {
                                    SetCrossfadeDistance(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 2, 2);
                                }
                            } else if (containsDistanceClose && containsDistanceFar) {
                                if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Close) {
                                    SetCrossfadeDistance(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 2, 1);
                                } else if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Far) {
                                    SetCrossfadeDistance(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 2, 2);
                                }
                            } else if (containsIntensitySoft && containsIntensityMedium && containsIntensityHard) {
                                if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Soft) {
                                    SetCrossfadeIntensity(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 3, 1);
                                } else if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Medium) {
                                    SetCrossfadeIntensity(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 3, 2);
                                } else if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Hard) {
                                    SetCrossfadeIntensity(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 3, 3);
                                }
                            } else if (containsIntensitySoft && containsIntensityHard) {
                                if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Soft) {
                                    SetCrossfadeIntensity(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 2, 1);
                                } else if (crossfadeGroups[i].crossfadeParts[ii].crossfadePartType == CrossfadePartType.Hard) {
                                    SetCrossfadeIntensity(crossfadeGroups[i].crossfadeParts[ii].soundContainer, 2, 2);
                                }
                            }
                        }
                    }
                } else if (menuObject.presetType == PresetType.PresetSame) {
                    for (int i = 0; i < mTargets.Length; i++) {
                        Undo.RecordObject(mTargets[i], "Set to Preset Settings");
                        SoundContainerCopy.CopyTo(mTargets[i], menuObject.soundContainerPreset);
                        if (mTargets[i].name.ToLowerInvariant().Contains("loop")) {
                            mTargets[i].internals.data.loopEnabled = true;
                            mTargets[i].internals.data.followPosition = true;
                            mTargets[i].internals.data.randomStartPosition = true;
                            mTargets[i].internals.data.stopIfTransformIsNull = true;
                        }
                        EditorUtility.SetDirty(mTargets[i]);
                    }
                } else if (menuObject.presetType == PresetType.PresetMatchName) {
                    SoundPresetFind.FindAllSoundPresets();
                    if (SoundPresetFind.soundPresets != null) {
                        for (int i = 0; i < SoundPresetFind.soundPresets.Length; i++) {
                            SoundPresetBase soundPreset = SoundPresetFind.soundPresets[i];
                            if (soundPreset != null && !soundPreset.internals.disableAll) {
                                for (int ii = 0; ii < soundPreset.internals.soundPresetGroup.Length; ii++) {
                                    SoundPresetGroup soundPresetGroup = soundPreset.internals.soundPresetGroup[ii];
                                    if (soundPresetGroup != null && !soundPresetGroup.ShouldUseMatch(true)) {
                                        for (int iii = 0; iii < mTargets.Length; iii++) {
                                            if (soundPresetGroup.GetNameMatches(mTargets[iii].name, true)) {
                                                Undo.RecordObject(mTargets[iii], "Set to Preset Settings");
                                                SoundContainerCopy.CopyTo(mTargets[iii], soundPresetGroup.soundContainerPreset);
                                                if (soundPreset.internals.automaticLoop && mTargets[iii].name.ToLowerInvariant().Contains("loop")) {
                                                    mTargets[iii].internals.data.loopEnabled = true;
                                                    mTargets[iii].internals.data.followPosition = true;
                                                    mTargets[iii].internals.data.randomStartPosition = true;
                                                    mTargets[iii].internals.data.stopIfTransformIsNull = true;
                                                }
                                                Debug.Log($"Sonity: Preset \"" + soundPresetGroup.soundContainerPreset.name + $"\" is applied to \"" + mTargets[iii].name + "\"", mTargets[iii]);
                                                EditorUtility.SetDirty(mTargets[iii]);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            } catch {
                return;
            }
        }

        private static string GetNameToRemoveWithRightSeparatorChar(string fileName, string nameToRemove) {
            if (fileName.EndsWith("_" + nameToRemove)) {
                return "_" + nameToRemove;
            } else if (fileName.EndsWith(" " + nameToRemove)) {
                return " " + nameToRemove;
            } else if (fileName.EndsWith("-" + nameToRemove)) {
                return "-" + nameToRemove;
            }
            return "_" + nameToRemove;
        }

        private CrossfadePartType NameIsCrossfade(ref string name) {

            name = name.ToLowerInvariant();
            name = RemoveEndString(name, "sc");
            name = RemoveTrailingJunk(name, false);
            name = RemoveEndString(name, "loop");
            name = RemoveTrailingJunk(name, false);

            CrossfadePartType tempCrossfadeType = CrossfadePartType.None;

            if (name.EndsWith("close")) {
                tempCrossfadeType = CrossfadePartType.Close;
            } else if (name.EndsWith("distant")) {
                tempCrossfadeType = CrossfadePartType.Distant;
            } else if (name.EndsWith("far")) {
                tempCrossfadeType = CrossfadePartType.Far;
            } else if (name.EndsWith("soft")) {
                tempCrossfadeType = CrossfadePartType.Soft;
            } else if (name.EndsWith("medium")) {
                tempCrossfadeType = CrossfadePartType.Medium;
            } else if (name.EndsWith("hard")) {
                tempCrossfadeType = CrossfadePartType.Hard;
            }

            name = RemoveEndString(name, tempCrossfadeType.ToString().ToLowerInvariant());
            name = RemoveTrailingJunk(name, false);

            return tempCrossfadeType;
        }

        enum CrossfadePartType {
            None = 0,
            Close = 1,
            Distant = 2,
            Far = 3,
            Soft = 4,
            Medium = 5,
            Hard = 6,
        }

        class CrossfadePart {
            public CrossfadePartType crossfadePartType = CrossfadePartType.None;
            public SoundContainerBase soundContainer;
        }

        class CrossfadeGroup {
            public string name = "";
            public List<CrossfadePart> crossfadeParts = new List<CrossfadePart>();
        }

        private void SetCrossfadeDistance(SoundContainerBase soundContainer, int layersOneIndexed, int thisIsOneIndexed) {
            Undo.RecordObject(soundContainer, "Automatic Crossfades");
            soundContainer.internals.data.SetVolumeDistanceCrossfade(layersOneIndexed, thisIsOneIndexed, true);
            EditorUtility.SetDirty(soundContainer);
        }

        private void SetCrossfadeIntensity(SoundContainerBase soundContainer, int layersOneIndexed, int thisIsOneIndexed) {
            Undo.RecordObject(soundContainer, "Automatic Crossfades");
            soundContainer.internals.data.SetVolumeIntensityCrossfade(layersOneIndexed, thisIsOneIndexed, true);
            EditorUtility.SetDirty(soundContainer);
        }

        private bool EndsWithCrossfade(string input) {
            if (input.EndsWith("close")) {
                return true;
            } else if (input.EndsWith("distant")) {
                return true;
            } else if (input.EndsWith("far")) {
                return true;
            } else if (input.EndsWith("soft")) {
                return true;
            } else if (input.EndsWith("medium")) {
                return true;
            } else if (input.EndsWith("hard")) {
                return true;
            }
            return false;
        }

        private string RemoveTrailingJunk(string input, bool removeNumbers) {
            char[] charsToRemove;
            if (removeNumbers) {
                charsToRemove = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ', '_', '-' };
            } else {
                charsToRemove = new char[] { ' ', '_', '-' };
            }
            return input.TrimEnd(charsToRemove);
        }

        private string RemoveEndString(string input, string toRemove) {
            if (input.EndsWith(toRemove)) {
                input = input.Remove(input.Length - toRemove.Length);
                
            }
            return input;
        }

        Vector2 distanceScaleVectorFromTo = new Vector2(100f, 50f);

        private void GuiSettingsBase() {

            StartBackgroundColor(EditorColor.GetSettings(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            EditorGUI.indentLevel = 1;

            BeginChange();
            EditorGuiFunction.DrawFoldout(settingsExpandBase, "Settings");
            EndChange();

            if (settingsExpandBase.boolValue) {

                // Distance Enabled
                BeginChange();
                EditorGUILayout.PropertyField(distanceEnabled, new GUIContent(EditorTextSoundContainer.enableDistanceLabel, EditorTextSoundContainer.enableDistanceTooltip));
                EndChange();

                // Distance Scale
                if (distanceEnabled.boolValue) {
                    EditorGUI.indentLevel++;
                    BeginChange();
                    EditorGUILayout.PropertyField(distanceScale, new GUIContent(EditorTextSoundContainer.distanceScaleLabel, EditorTextSoundContainer.distanceScaleTooltip));
                    if (distanceScale.floatValue < 0) {
                        distanceScale.floatValue = 0f;
                    }
                    EndChange();
                    if (distanceScale.floatValue == 0 && distanceEnabled.boolValue) {
                        EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.distanceScaleWarningLabel), EditorStyles.helpBox);
                    }

                    EditorGUI.indentLevel--;
                }

                if (mTargets.Length > 1) {

                    // Show distance scale ranges if select more than 1 object /////////////////////////////////////////////////////////
                    bool anyDistanceEnabled = false;
                    string minDistanceName = "";
                    string maxDistanceName = "";
                    float minDistance = Mathf.Infinity;
                    float maxDistance = 0f;
                    if (mTargets.Length > 1) {
                        // Finds the lowest and highest distance
                        for (int i = 0; i < mTargets.Length; i++) {
                            if (mTargets[i].internals.data.distanceEnabled) {
                                anyDistanceEnabled = true;
                                if (minDistance > mTargets[i].internals.data.distanceScale) {
                                    minDistance = mTargets[i].internals.data.distanceScale;
                                }
                                if (maxDistance < mTargets[i].internals.data.distanceScale) {
                                    maxDistance = mTargets[i].internals.data.distanceScale;
                                }
                            }
                        }
                        minDistanceName = "Min " + Mathf.FloorToInt(minDistance);
                        maxDistanceName = "Max " + Mathf.FloorToInt(maxDistance);
                    }

                    if (anyDistanceEnabled) {
                        EditorGUI.indentLevel++;
                        if (distanceEnabled.boolValue) {
                            EditorGUI.indentLevel++;
                        }

                        BeginChange();
                        distanceScaleVectorFromTo = EditorGUILayout.Vector2Field(new GUIContent(EditorTextSoundContainer.distanceScaleIfXSetToYValueLabel, EditorTextSoundContainer.distanceScaleIfXSetToYValueTooltip), distanceScaleVectorFromTo);
                        EndChange();

                        EditorGUILayout.BeginHorizontal();
                        // For offsetting the buttons to the right
                        EditorGUILayout.LabelField(new GUIContent(""), GUILayout.Width(EditorGUIUtility.labelWidth));

                        // Apply Distance Scale
                        BeginChange();
                        if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.distanceScaleIfXSetToYButtonLabel + " (" + minDistanceName + " " + maxDistanceName + ")", EditorTextSoundContainer.distanceScaleIfXSetToYButtonTooltip))) {
                            for (int i = 0; i < mTargets.Length; i++) {
                                if (mTargets[i].internals.data.distanceEnabled) {
                                    Undo.RecordObject(mTargets[i], "Distance Scale If X Set to Y");
                                    if (mTargets[i].internals.data.distanceScale == distanceScaleVectorFromTo.x) {
                                        mTargets[i].internals.data.distanceScale = distanceScaleVectorFromTo.y;
                                    }
                                    EditorUtility.SetDirty(mTargets[i]);
                                }
                            }
                        }
                        EndChange();
                        EditorGUILayout.EndHorizontal();

                        EditorGUI.indentLevel--;
                        if (distanceEnabled.boolValue) {
                            EditorGUI.indentLevel--;
                        }
                    }
                }

                // Loop
                BeginChange();
                bool tempLoopEnabled = EditorGUILayout.Toggle(new GUIContent(EditorTextSoundContainer.loopLabel, EditorTextSoundContainer.loopTooltip), loopEnabled.boolValue);
                // Automatically change random start position and stop if transform is null
                if (loopEnabled.boolValue != tempLoopEnabled) {
                    loopEnabled.boolValue = tempLoopEnabled;
                    if (tempLoopEnabled) {
                        followPosition.boolValue = true;
                        stopIfTransformIsNull.boolValue = true;
                        randomStartPosition.boolValue = true;
                        //virtualize.boolValue = true; // Virtualize Todo
                    } else {
                        followPosition.boolValue = false;
                        stopIfTransformIsNull.boolValue = false;
                        randomStartPosition.boolValue = false;
                        //virtualize.boolValue = false; // Virtualize Todo
                    }
                }
                EndChange();

                // Follow Position
                BeginChange();
                EditorGUILayout.PropertyField(followPosition, new GUIContent(EditorTextSoundContainer.followPositionLabel, EditorTextSoundContainer.followPositionTooltip));
                EndChange();

                // Stop if Transform is null
                BeginChange();
                EditorGUILayout.PropertyField(stopIfTransformIsNull, new GUIContent(EditorTextSoundContainer.stopIfTransformIsNullLabel, EditorTextSoundContainer.stopIfTransformIsNullTooltip));
                EndChange();

                // Virtualize Todo
                //// Virtualize
                //BeginChange();
                //EditorGUILayout.PropertyField(virtualize, new GUIContent(EditorTextSoundContainer.virtualizeLabel, EditorTextSoundContainer.virtualizeTooltip));
                //EndChange();

                // Random Start Position
                BeginChange();
                EditorGUILayout.PropertyField(randomStartPosition, new GUIContent(EditorTextSoundContainer.randomStartPositionLabel, EditorTextSoundContainer.randomStartPositionTooltip));
                EndChange();

                if (randomStartPosition.boolValue) {
                    EditorGUI.indentLevel++;
                    // Random Start Position Min Max
                    float min = randomStartPositionMin.floatValue;
                    float max = randomStartPositionMax.floatValue;
                    BeginChange();
                    EditorGUILayout.MinMaxSlider(
                        new GUIContent(
                            EditorTextSoundContainer.randomStartPositionMinMaxLabel, 
                            EditorTextSoundContainer.randomStartPositionMinMaxTooltip), 
                        ref min, ref max, 0f, 1f);
                    randomStartPositionMin.floatValue = min;
                    randomStartPositionMax.floatValue = max;
                    EndChange();
                    EditorGUI.indentLevel--;
                } else {
                    // Start Offset
                    BeginChange();
                    EditorGUILayout.Slider(startPosition, 0f, 1f, new GUIContent(EditorTextSoundContainer.startPositionLabel, EditorTextSoundContainer.startPositionTooltip));
                    EndChange();
                }

                BeginChange();
                EditorGuiFunction.DrawFoldout(settingsExpandAdvanced, "Advanced", "", 0, false, false, true);
                EndChange();

                if (settingsExpandAdvanced.boolValue) {

                    // Reverse
                    BeginChange();
                    bool tempReverse = EditorGUILayout.Toggle(new GUIContent(EditorTextSoundContainer.reverseLabel, EditorTextSoundContainer.reverseTooltip), reverse.boolValue);
                    if (reverse.boolValue != tempReverse) {
                        reverse.boolValue = tempReverse;
                        startPosition.floatValue = 1f - startPosition.floatValue;
                    }
                    EndChange();

                    // Play Order
                    BeginChange();
                    EditorGUILayout.PropertyField(playOrder, new GUIContent(EditorTextSoundContainer.playOrderLabel, EditorTextSoundContainer.playOrderTooltip));
                    EndChange();

                    // Priority
                    BeginChange();
                    EditorGUILayout.Slider(priority, 0f, 1f, new GUIContent(EditorTextSoundContainer.priorityLabel, EditorTextSoundContainer.priorityTooltip));
                    EndChange();

                    // Lock Axis Enable
                    BeginChange();
                    EditorGUILayout.PropertyField(lockAxisEnable, new GUIContent(EditorTextSoundContainer.lockAxisEnableLabel, EditorTextSoundContainer.lockAxisEnableTooltip));
                    EndChange();

                    if (lockAxisEnable.boolValue) {
                        EditorGUI.indentLevel++;
                        // Lock Axis
                        BeginChange();
                        EditorGUILayout.PropertyField(lockAxis, new GUIContent(EditorTextSoundContainer.lockAxisLabel, EditorTextSoundContainer.lockAxisTooltip));
                        EndChange();
                        // Lock Axis Value
                        BeginChange();
                        EditorGUILayout.PropertyField(lockAxisPosition, new GUIContent(EditorTextSoundContainer.lockAxisPositionLabel, EditorTextSoundContainer.lockAxisPositionTooltip));
                        EndChange();
                        EditorGUI.indentLevel--;
                    }

                    // Prevent End Clicks
                    BeginChange();
                    EditorGUILayout.PropertyField(preventEndClicks, new GUIContent(EditorTextSoundContainer.preventEndClicksLabel, EditorTextSoundContainer.preventEndClicksTooltip));
                    EndChange();

                    // Never Steal Voice
                    BeginChange();
                    EditorGUILayout.PropertyField(neverStealVoice, new GUIContent(EditorTextSoundContainer.neverStealVoiceLabel, EditorTextSoundContainer.neverStealVoiceTooltip));
                    EndChange();

                    // Never Steal Voice Effects
                    BeginChange();
                    EditorGUILayout.PropertyField(neverStealVoiceEffects, new GUIContent(EditorTextSoundContainer.neverStealVoiceEffectsLabel, EditorTextSoundContainer.neverStealVoiceEffectsTooltip));
                    EndChange();

                    // Doppler
                    BeginChange();
                    EditorGUILayout.Slider(dopplerAmount, 0f, 5f, new GUIContent(EditorTextSoundContainer.dopplerAmountLabel, EditorTextSoundContainer.dopplerAmountTooltip));
                    EndChange();

                    // Bypass Reverb Zones
                    BeginChange();
                    EditorGUILayout.PropertyField(bypassReverbZones, new GUIContent(EditorTextSoundContainer.bypassReverbZonesLabel, EditorTextSoundContainer.bypassReverbZonesTooltip));
                    EndChange();

                    // Bypass AudioSource Effects
                    BeginChange();
                    EditorGUILayout.PropertyField(bypassVoiceEffects, new GUIContent(EditorTextSoundContainer.bypassVoiceEffectsLabel, EditorTextSoundContainer.bypassVoiceEffectsTooltip));
                    EndChange();

                    // Bypass Listener Effects
                    BeginChange();
                    EditorGUILayout.PropertyField(bypassListenerEffects, new GUIContent(EditorTextSoundContainer.bypassListenerEffectsLabel, EditorTextSoundContainer.bypassListenerEffectsTooltip));
                    EndChange();

                    // HRTF Plugin Spatialize
                    BeginChange();
                    EditorGUILayout.PropertyField(hrtfPluginSpatialize, new GUIContent(EditorTextSoundContainer.hrtfPluginSpatializeLabel, EditorTextSoundContainer.hrtfPluginSpatializeTooltip));
                    EndChange();

                    // HRTF Plugin Spatialize Post Effects
                    BeginChange();
                    EditorGUILayout.PropertyField(hrtfPluginSpatializePostEffects, new GUIContent(EditorTextSoundContainer.hrtfPluginSpatializePostEffectsLabel, EditorTextSoundContainer.hrtfPluginSpatializePostEffectsTooltip));
                    EndChange();
                }

                // Sonity Steam Audio
#if SONITY_ENABLE_INTEGRATION_STEAM_AUDIO
                // Show spatialize settings even if advanced menu is closed
                if (!settingsExpandAdvanced.boolValue) {
                    // HRTF Plugin Spatialize
                    BeginChange();
                    EditorGUILayout.PropertyField(hrtfPluginSpatialize, new GUIContent(EditorTextSoundContainer.hrtfPluginSpatializeLabel, EditorTextSoundContainer.hrtfPluginSpatializeTooltip));
                    EndChange();

                    // HRTF Plugin Spatialize Post Effects
                    BeginChange();
                    EditorGUILayout.PropertyField(hrtfPluginSpatializePostEffects, new GUIContent(EditorTextSoundContainer.hrtfPluginSpatializePostEffectsLabel, EditorTextSoundContainer.hrtfPluginSpatializePostEffectsTooltip));
                    EndChange();
                }
#endif

                // Sonity Steam Audio
#if SONITY_ENABLE_INTEGRATION_STEAM_AUDIO

                BeginChange();
                EditorGuiFunction.DrawFoldout(steamAudioExpand, "Steam Audio", "", 0, false, false, true);
                EndChange();

                if (steamAudioExpand.boolValue) {

                    // For this part of the script copied and modified from Steam Audio there is a different license:
                    // Copyright 2017-2023 Valve Corporation.
                    // Licensed under the Apache License, Version 2.0 (the "License");
                    // you may not use this file except in compliance with the License.
                    // You may obtain a copy of the License at
                    // http://www.apache.org/licenses/LICENSE-2.0
                    // Unless required by applicable law or agreed to in writing, software
                    // distributed under the License is distributed on an "AS IS" BASIS,
                    // WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
                    // See the License for the specific language governing permissions and
                    // limitations under the License.

                    // Copied from SteamAudioSourceInspector

                    var audioEngineIsUnity = (SteamAudioSettings.Singleton.audioEngine == AudioEngineType.Unity);

                    serializedObject.Update();

                    // HRTF Settings
                    if (audioEngineIsUnity) {
                        EditorGUILayout.PropertyField(mDirectBinaural, new GUIContent(EditorTextSteamAudio.directBinauralLabel, EditorTextSteamAudio.directBinauralTooltip));
                        EditorGUILayout.PropertyField(mInterpolation, new GUIContent(EditorTextSteamAudio.interpolationLabel, EditorTextSteamAudio.interpolationTooltip));
                    }

                    if (audioEngineIsUnity && SteamAudioSettings.Singleton.perspectiveCorrection) {
                        EditorGUILayout.PropertyField(mPerspectiveCorrection, new GUIContent(EditorTextSteamAudio.perspectiveCorrectionLabel, EditorTextSteamAudio.interpolationTooltip));
                    }

                    // Distance Attenuation
                    if (audioEngineIsUnity) {
                        EditorGUILayout.PropertyField(mDistanceAttenuation, new GUIContent(EditorTextSteamAudio.distanceAttenuationLabel, EditorTextSteamAudio.distanceAttenuationTooltip));
                        if (mDistanceAttenuation.boolValue) {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.PropertyField(mDistanceAttenuationInput, new GUIContent(EditorTextSteamAudio.distanceAttenuationInputLabel, EditorTextSteamAudio.distanceAttenuationInputTooltip));
                            EditorGUI.indentLevel--;
                            EditorGUILayout.Separator();
                        }
                    }

                    // Air Absorption
                    if (audioEngineIsUnity) {
                        EditorGUILayout.PropertyField(mAirAbsorption, new GUIContent(EditorTextSteamAudio.airAbsorptionLabel, EditorTextSteamAudio.airAbsorptionTooltip));
                        if (mAirAbsorption.boolValue) {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.PropertyField(mAirAbsorptionInput, new GUIContent(EditorTextSteamAudio.airAbsorptionInputLabel, EditorTextSteamAudio.airAbsorptionInputTooltip));
                            if ((AirAbsorptionInput)mAirAbsorptionInput.enumValueIndex == AirAbsorptionInput.UserDefined) {
                                EditorGUI.indentLevel++;
                                EditorGUILayout.PropertyField(mAirAbsorptionLow, new GUIContent(EditorTextSteamAudio.airAbsorptionLowLabel, EditorTextSteamAudio.airAbsorptionLowTooltip));
                                EditorGUILayout.PropertyField(mAirAbsorptionMid, new GUIContent(EditorTextSteamAudio.airAbsorptionMidLabel, EditorTextSteamAudio.airAbsorptionMidTooltip));
                                EditorGUILayout.PropertyField(mAirAbsorptionHigh, new GUIContent(EditorTextSteamAudio.airAbsorptionHighLabel, EditorTextSteamAudio.airAbsorptionHighTooltip));
                                EditorGUI.indentLevel--;
                            }
                            EditorGUI.indentLevel--;
                            EditorGUILayout.Separator();
                        }
                    }

                    // Directivity
                    if (audioEngineIsUnity) {
                        EditorGUILayout.PropertyField(mDirectivity, new GUIContent(EditorTextSteamAudio.directivityLabel, EditorTextSteamAudio.directivityTooltip));
                        if (mDirectivity.boolValue) {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.PropertyField(mDirectivityInput, new GUIContent(EditorTextSteamAudio.directivityInputLabel, EditorTextSteamAudio.directivityInputTooltip));
                            if ((DirectivityInput)mDirectivityInput.enumValueIndex == DirectivityInput.SimulationDefined) {
                                EditorGUILayout.PropertyField(mDipoleWeight, new GUIContent(EditorTextSteamAudio.dipoleWeightLabel, EditorTextSteamAudio.dipoleWeightTooltip));
                                EditorGUILayout.PropertyField(mDipolePower, new GUIContent(EditorTextSteamAudio.dipolePowerLabel, EditorTextSteamAudio.dipolePowerTooltip));
                                DrawDirectivity(mDipoleWeight.floatValue, mDipolePower.floatValue);
                            } else if ((DirectivityInput)mDirectivityInput.enumValueIndex == DirectivityInput.UserDefined) {
                                EditorGUILayout.PropertyField(mDirectivityValue, new GUIContent(EditorTextSteamAudio.directivityValueLabel, EditorTextSteamAudio.directivityValueTooltip));
                            }
                            EditorGUI.indentLevel--;
                            EditorGUILayout.Separator();
                        }
                    }

                    // Occlusion
                    EditorGUILayout.PropertyField(mOcclusion, new GUIContent(EditorTextSteamAudio.occlusionLabel, EditorTextSteamAudio.occlusionTooltip));
                    if (mOcclusion.boolValue) {
                        EditorGUI.indentLevel++;
                        if (audioEngineIsUnity) {
                            EditorGUILayout.PropertyField(mOcclusionInput, new GUIContent(EditorTextSteamAudio.occlusionInputLabel, EditorTextSteamAudio.occlusionInputTooltip));
                        }

                        if (!audioEngineIsUnity || (OcclusionInput)mOcclusionInput.enumValueIndex == OcclusionInput.SimulationDefined) {
                            EditorGUILayout.PropertyField(mOcclusionType, new GUIContent(EditorTextSteamAudio.occlusionTypeLabel, EditorTextSteamAudio.occlusionTypeTooltip));
                            if ((OcclusionType)mOcclusionType.enumValueIndex == OcclusionType.Volumetric) {
                                EditorGUILayout.PropertyField(mOcclusionRadius, new GUIContent(EditorTextSteamAudio.occlusionRadiusLabel, EditorTextSteamAudio.occlusionRadiusTooltip));
                                EditorGUILayout.PropertyField(mOcclusionSamples, new GUIContent(EditorTextSteamAudio.occlusionSamplesLabel, EditorTextSteamAudio.occlusionSamplesTooltip));
                            }
                        } else if ((OcclusionInput)mOcclusionInput.enumValueIndex == OcclusionInput.UserDefined) {
                            EditorGUILayout.PropertyField(mOcclusionValue, new GUIContent(EditorTextSteamAudio.occlusionTypeLabel, EditorTextSteamAudio.occlusionTypeTooltip));
                        }

                        EditorGUILayout.PropertyField(mTransmission, new GUIContent(EditorTextSteamAudio.transmissionLabel, EditorTextSteamAudio.transmissionTooltip));
                        if (audioEngineIsUnity) {
                            if (mTransmission.boolValue) {
                                EditorGUI.indentLevel++;
                                EditorGUILayout.PropertyField(mTransmissionType, new GUIContent(EditorTextSteamAudio.transmissionTypeLabel, EditorTextSteamAudio.transmissionTypeTooltip));
                                EditorGUILayout.PropertyField(mTransmissionInput, new GUIContent(EditorTextSteamAudio.transmissionInputLabel, EditorTextSteamAudio.transmissionInputTooltip));
                                if ((TransmissionInput)mTransmissionInput.enumValueIndex == TransmissionInput.UserDefined) {
                                    EditorGUI.indentLevel++;
                                    if (mTransmissionType.enumValueIndex == (int)TransmissionType.FrequencyDependent) {
                                        EditorGUILayout.PropertyField(mTransmissionLow, new GUIContent(EditorTextSteamAudio.transmissionLowLabel, EditorTextSteamAudio.transmissionLowTooltip));
                                        EditorGUILayout.PropertyField(mTransmissionMid, new GUIContent(EditorTextSteamAudio.transmissionMidLabel, EditorTextSteamAudio.transmissionMidTooltip));
                                        EditorGUILayout.PropertyField(mTransmissionHigh, new GUIContent(EditorTextSteamAudio.transmissionHighLabel, EditorTextSteamAudio.transmissionHighTooltip));
                                    } else {
                                        // Custom name and tooltip for single band
                                        EditorGUILayout.PropertyField(mTransmissionMid, new GUIContent(EditorTextSteamAudio.transmissionSingleBandAttenuationLabel, EditorTextSteamAudio.transmissionSingleBandAttenuationTooltip));
                                    }
                                    EditorGUI.indentLevel--;
                                } else if ((TransmissionInput)mTransmissionInput.enumValueIndex == TransmissionInput.SimulationDefined) {
                                    EditorGUILayout.PropertyField(mTransmissionRays, new GUIContent(EditorTextSteamAudio.transmissionRaysLabel, EditorTextSteamAudio.transmissionRaysTooltip));
                                }
                                EditorGUI.indentLevel--;
                            }
                        }
                        EditorGUI.indentLevel--;
                        EditorGUILayout.Separator();
                    }

                    // Direct Mix Level
                    if (audioEngineIsUnity) {
                        EditorGUILayout.PropertyField(mDirectMixLevel, new GUIContent(EditorTextSteamAudio.directMixLevelLabel, EditorTextSteamAudio.directMixLevelTooltip));
                    }

                    // Reflections
                    EditorGUILayout.PropertyField(mReflections, new GUIContent(EditorTextSteamAudio.reflectionsLabel, EditorTextSteamAudio.reflectionsTooltip));
                    if (mReflections.boolValue) {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(mReflectionsType, new GUIContent(EditorTextSteamAudio.reflectionsTypeLabel, EditorTextSteamAudio.reflectionsTypeTooltip));

                        if (audioEngineIsUnity && mDistanceAttenuation.boolValue 
                            && (DistanceAttenuationInput)mDistanceAttenuationInput.enumValueIndex == DistanceAttenuationInput.CurveDriven) {
                            EditorGUILayout.PropertyField(mUseDistanceCurveForReflections, new GUIContent(EditorTextSteamAudio.useDistanceCurveForReflectionsLabel, EditorTextSteamAudio.useDistanceCurveForReflectionsTooltip));
                        }

                        if ((ReflectionsType)mReflectionsType.enumValueIndex == ReflectionsType.BakedStaticSource) {
                            EditorGUILayout.PropertyField(mCurrentBakedSource, new GUIContent(EditorTextSteamAudio.currentBakedSourceLabel, EditorTextSteamAudio.currentBakedSourceTooltip));
                        }

                        if (audioEngineIsUnity) {
                            EditorGUILayout.PropertyField(mApplyHRTFToReflections, new GUIContent(EditorTextSteamAudio.applyHRTFToReflectionsLabel, EditorTextSteamAudio.applyHRTFToReflectionsTooltip));
                            EditorGUILayout.PropertyField(mReflectionsMixLevel, new GUIContent(EditorTextSteamAudio.reflectionsMixLevelLabel, EditorTextSteamAudio.reflectionsMixLevelTooltip));
                        }
                        EditorGUI.indentLevel--;
                        EditorGUILayout.Separator();
                    }

                    // Pathing
                    EditorGUILayout.PropertyField(mPathing, new GUIContent(EditorTextSteamAudio.pathingLabel, EditorTextSteamAudio.pathingTooltip));
                    if (mPathing.boolValue) {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(mPathingProbeBatch, new GUIContent(EditorTextSteamAudio.pathingProbeBatchLabel, EditorTextSteamAudio.pathingProbeBatchTooltip));
                        EditorGUILayout.PropertyField(mPathValidation, new GUIContent(EditorTextSteamAudio.pathValidationLabel, EditorTextSteamAudio.pathValidationTooltip));
                        EditorGUILayout.PropertyField(mFindAlternatePaths, new GUIContent(EditorTextSteamAudio.findAlternatePathsLabel, EditorTextSteamAudio.findAlternatePathsTooltip));

                        if (audioEngineIsUnity) {
                            EditorGUILayout.PropertyField(mApplyHRTFToPathing, new GUIContent(EditorTextSteamAudio.applyHRTFToPathingLabel, EditorTextSteamAudio.applyHRTFToPathingTooltip));
                            EditorGUILayout.PropertyField(mPathingMixLevel, new GUIContent(EditorTextSteamAudio.pathingMixLevelLabel, EditorTextSteamAudio.pathingMixLevelTooltip));
                        }
                        EditorGUI.indentLevel--;
                    }

                    serializedObject.ApplyModifiedProperties();

                    EditorGUILayout.BeginHorizontal();
                    // For offsetting the buttons to the right
                    EditorGUILayout.LabelField(new GUIContent(""), GUILayout.Width(EditorGUIUtility.labelWidth));
                    // Reset Steam Audio
                    BeginChange();
                    if (GUILayout.Button(new GUIContent("Reset Steam Audio", ""))) {
                        for (int i = 0; i < mTargets.Length; i++) {
                            Undo.RecordObject(mTargets[i], "Reset Steam Audio");
                            mTargets[i].internals.data.steamAudio = new SoundContainerInternalsDataSteamAudio();
                            mTargets[i].internals.data.steamAudio.steamAudioExpand = true;
                            EditorUtility.SetDirty(mTargets[i]);
                        }
                    }
                    EndChange();
                    EditorGUILayout.EndHorizontal();
                    // End of Steam Audio license, resuming original Sonigon copyright
                }
#endif
            }
            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        // Sonity Steam Audio
#if SONITY_ENABLE_INTEGRATION_STEAM_AUDIO
        // For this part of the script copied and modified from Steam Audio there is a different license
        // Copyright 2017-2023 Valve Corporation.
        // Licensed under the Apache License, Version 2.0 (the "License");
        // you may not use this file except in compliance with the License.
        // You may obtain a copy of the License at
        // http://www.apache.org/licenses/LICENSE-2.0
        // Unless required by applicable law or agreed to in writing, software
        // distributed under the License is distributed on an "AS IS" BASIS,
        // WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
        // See the License for the specific language governing permissions and
        // limitations under the License.

        Texture2D mDirectivityPreview = null;
        float[] mDirectivitySamples = null;
        Vector2[] mDirectivityPositions = null;

        void DrawDirectivity(float dipoleWeight, float dipolePower) {
            if (mDirectivityPreview == null) {
                mDirectivityPreview = new Texture2D(65, 65);
            }

            if (mDirectivitySamples == null) {
                mDirectivitySamples = new float[360];
                mDirectivityPositions = new Vector2[360];
            }

            for (var i = 0; i < mDirectivitySamples.Length; ++i) {
                var theta = (i / 360.0f) * (2.0f * Mathf.PI);
                mDirectivitySamples[i] = Mathf.Pow(Mathf.Abs((1.0f - dipoleWeight) + dipoleWeight * Mathf.Cos(theta)), dipolePower);

                var r = 31 * Mathf.Abs(mDirectivitySamples[i]);
                var x = r * Mathf.Cos(theta) + 32;
                var y = r * Mathf.Sin(theta) + 32;
                mDirectivityPositions[i] = new Vector2(-y, x);
            }

            for (var v = 0; v < mDirectivityPreview.height; ++v) {
                for (var u = 0; u < mDirectivityPreview.width; ++u) {
                    mDirectivityPreview.SetPixel(u, v, Color.gray);
                }
            }

            for (var u = 0; u < mDirectivityPreview.width; ++u) {
                mDirectivityPreview.SetPixel(u, 32, Color.black);
            }

            for (var v = 0; v < mDirectivityPreview.height; ++v) {
                mDirectivityPreview.SetPixel(32, v, Color.black);
            }

            for (var i = 0; i < mDirectivitySamples.Length; ++i) {
                var color = (mDirectivitySamples[i] > 0.0f) ? Color.red : Color.blue;
                mDirectivityPreview.SetPixel((int)mDirectivityPositions[i].x, (int)mDirectivityPositions[i].y, color);
            }

            mDirectivityPreview.Apply();

            //EditorGUILayout.PrefixLabel("Preview");
            EditorGUILayout.Space();
            var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect());
            var center = rect.center;
            center.x += 4;
            rect.center = center;
            rect.width = 65;
            rect.height = 65;

            EditorGUILayout.Space();
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            EditorGUILayout.Space();

            EditorGUI.DrawPreviewTexture(rect, mDirectivityPreview);
        }
        // End of Steam Audio license, resuming original Sonigon copyright
#endif

        private void GuiVolume() {

            // Volume
            StartBackgroundColor(EditorColor.GetVolumeMax(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));
            
            BeginChange();
            EditorGuiFunction.DrawFoldout(volumeExpand, "Volume");
            EndChange();

            if (volumeExpand.boolValue) {

                BeginChange();
                EditorGUILayout.Slider(volumeDecibel, VolumeScale.lowestVolumeDecibel, VolumeScale.volumeIncrease24dbMaxDecibel, new GUIContent(EditorTextSoundContainer.volumeLabel, EditorTextSoundContainer.volumeTooltip));
                if (volumeDecibel.floatValue <= VolumeScale.lowestVolumeDecibel) {
                    volumeDecibel.floatValue = Mathf.NegativeInfinity;
                }
                if (volumeRatio.floatValue != VolumeScale.ConvertDecibelToRatio(volumeDecibel.floatValue)) {
                    volumeRatio.floatValue = VolumeScale.ConvertDecibelToRatio(volumeDecibel.floatValue);
                }
                EndChange();

                // Lower volume 1 dB
                EditorGUILayout.BeginHorizontal();
                // For offsetting the buttons to the right
                EditorGUILayout.LabelField(new GUIContent(""), GUILayout.Width(EditorGUIUtility.labelWidth));
                
                string minVolumeName = "";
                if (mTargets.Length > 1) {
                    // Finds the lowest volume in dB
                    float minVolumeDecibel = Mathf.Infinity;
                    for (int i = 0; i < mTargets.Length; i++) {
                        if (minVolumeDecibel > mTargets[i].internals.data.volumeDecibel) {
                            minVolumeDecibel = mTargets[i].internals.data.volumeDecibel;
                        }
                    }
                    if (minVolumeDecibel < VolumeScale.lowestVolumeDecibel) {
                        minVolumeName = " (Min " + "-Infinity" + ")";
                    } else {
                        minVolumeName = " (Min " + Mathf.FloorToInt(minVolumeDecibel) + ")";
                    }
                }
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.volumeRelativeLowerLabel + minVolumeName, EditorTextSoundContainer.volumeRelativeLowerTooltip))) {
                    for (int i = 0; i < mTargets.Length; i++) {
                        Undo.RecordObject(mTargets[i], "Volume -1 dB");
                        // Don't clamp max when lowering volume so you always can lower it
                        mTargets[i].internals.data.volumeDecibel = Mathf.Clamp(mTargets[i].internals.data.volumeDecibel - 1f, VolumeScale.lowestVolumeDecibel, Mathf.Infinity);
                        mTargets[i].internals.data.volumeRatio = VolumeScale.ConvertDecibelToRatio(mTargets[i].internals.data.volumeDecibel);
                        EditorUtility.SetDirty(mTargets[i]);
                    }
                }
                EndChange();

                string maxVolumeName = "";
                if (mTargets.Length > 1) {
                    // Finds the highest volume in dB
                    float maxVolumeDecibel = Mathf.NegativeInfinity;
                    for (int i = 0; i < mTargets.Length; i++) {
                        if (maxVolumeDecibel < mTargets[i].internals.data.volumeDecibel) {
                            maxVolumeDecibel = mTargets[i].internals.data.volumeDecibel;
                        }
                    }
                    if (maxVolumeDecibel < VolumeScale.lowestVolumeDecibel) {
                        maxVolumeName = " (Max " + "-Infinity" + ")";
                    } else {
                        maxVolumeName = " (Max " + Mathf.FloorToInt(maxVolumeDecibel) + ")";
                    }
                }
                // Increase volume 1 dB
                BeginChange();
                if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.volumeRelativeIncreaseLabel + maxVolumeName, EditorTextSoundContainer.volumeRelativeIncreaseTooltip))) {
                    // Finds the highest volume in dB
                    float maxVolumeDecibel = Mathf.NegativeInfinity;
                    for (int i = 0; i < mTargets.Length; i++) {
                        if (maxVolumeDecibel < mTargets[i].internals.data.volumeDecibel) {
                            maxVolumeDecibel = mTargets[i].internals.data.volumeDecibel;
                        }
                    }
                    for (int i = 0; i < mTargets.Length; i++) {
                        Undo.RecordObject(mTargets[i], "Volume +1 dB");
                        mTargets[i].internals.data.volumeDecibel = Mathf.Clamp(mTargets[i].internals.data.volumeDecibel + 1f, VolumeScale.lowestVolumeDecibel, VolumeScale.volumeIncrease24dbMaxDecibel);
                        mTargets[i].internals.data.volumeRatio = VolumeScale.ConvertDecibelToRatio(mTargets[i].internals.data.volumeDecibel);
                        EditorUtility.SetDirty(mTargets[i]);
                    }
                }
                EndChange();
                EditorGUILayout.EndHorizontal();

                // Warning if volume is over 0
                for (int i = 0; i < mTargets.Length; i++) {
                    if (mTargets[i].internals.data.volumeRatio > (VolumeScale.volumeIncrease24dbMaxRatio + 0.00001)) {
                        EditorGUILayout.HelpBox(EditorTextSoundContainer.volumeOverLimitWarning, MessageType.Warning);
                        break;
                    }
                }

                // Random Volume
                BeginChange();
                EditorGUILayout.PropertyField(volumeRandomEnable, new GUIContent(EditorTextSoundContainer.volumeRandomLabel, EditorTextSoundContainer.volumeRandomTooltip));
                EndChange();

                if (volumeRandomEnable.boolValue) {
                    EditorGUI.indentLevel++;
                    BeginChange();
                    EditorGUILayout.Slider(volumeRandomRangeDecibel, -36f, 0f, new GUIContent(EditorTextSoundContainer.volumeRandomRangeLabel, EditorTextSoundContainer.volumeRandomRangeTooltip));
                    EndChange();
                    EditorGUI.indentLevel--;
                }

                if (distanceEnabled.boolValue) {
                    EditorGUILayout.Separator();
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.volumeDistanceLabel, EditorTextSoundContainer.volumeDistanceTooltip));

                    EditorGUI.indentLevel++;
                    BeginChange();
                    EditorGUILayout.Slider(volumeDistanceRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.volumeDistanceRolloffLabel, EditorTextSoundContainer.volumeDistanceRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(volumeDistanceCurve, EditorColor.GetVolumeMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.volumeDistanceCurveLabel, EditorTextSoundContainer.volumeDistanceCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();
                    EditorGUI.indentLevel--;

                    EditorGUILayout.Separator();
                    // Distance Volume Crossfade
                    GuiVolumeDistanceCrossfade();

                    // Preview Curve
                    curveDraw.Draw(EditorSoundContainerCurveType.Volume, EditorSoundContainerCurveValue.Distance);
                } 

                EditorGUILayout.Separator();

                BeginChange();
                EditorGUILayout.PropertyField(volumeIntensityEnable, new GUIContent(EditorTextSoundContainer.volumeIntensityEnableLabel, EditorTextSoundContainer.volumeIntensityEnableTooltip));
                EndChange();

                if (volumeIntensityEnable.boolValue) {

                    EditorGUI.indentLevel++;
                    BeginChange();
                    EditorGUILayout.Slider(volumeIntensityRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.volumeIntensityRolloffLabel, EditorTextSoundContainer.volumeIntensityRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.Slider(volumeIntensityStrength, 0f, 1f, new GUIContent(EditorTextSoundContainer.volumeIntensityStrengthLabel, EditorTextSoundContainer.volumeIntensityStrengthTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(volumeIntensityCurve, EditorColor.GetVolumeMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.volumeIntensityCurveLabel, EditorTextSoundContainer.volumeIntensityCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();
                    EditorGUI.indentLevel--;

                    EditorGUILayout.Separator();
                    // Intensity Volume Crossfade
                    GuiVolumeIntensityCrossfade();

                    curveDraw.Draw(EditorSoundContainerCurveType.Volume, EditorSoundContainerCurveValue.Intensity);
                }
            }
            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        private void GuiVolumeDistanceCrossfade() {
            // Distance Crossfade
            if (distanceEnabled.boolValue) {

                BeginChange();
                EditorGUILayout.PropertyField(volumeDistanceCrossfadeEnable, new GUIContent(EditorTextSoundContainer.volumeDistanceCrossfadeEnabledLabel, EditorTextSoundContainer.volumeDistanceCrossfadeEnabledTooltip));
                EndChange();

                if (volumeDistanceCrossfadeEnable.boolValue) {

                    EditorGUI.indentLevel++;
                    // Total Number of Layers 
                    BeginChange();
                    EditorGUILayout.PropertyField(volumeDistanceCrossfadeTotalLayersOneBased, new GUIContent(EditorTextSoundContainer.volumeDistanceCrossfadeLayersLabel, EditorTextSoundContainer.volumeDistanceCrossfadeLayersTooltip));
                    volumeDistanceCrossfadeTotalLayersOneBased.intValue = Mathf.Clamp(volumeDistanceCrossfadeTotalLayersOneBased.intValue, 2, int.MaxValue);
                    volumeDistanceCrossfadeTotalLayers.intValue = volumeDistanceCrossfadeTotalLayersOneBased.intValue - 1;
                    volumeDistanceCrossfadeTotalLayers.intValue = Mathf.Clamp(volumeDistanceCrossfadeTotalLayers.intValue, 1, int.MaxValue);
                    // So that the current layer is not more than total number of layers
                    volumeDistanceCrossfadeLayerOneBased.intValue = Mathf.Clamp(volumeDistanceCrossfadeLayerOneBased.intValue, 0, volumeDistanceCrossfadeTotalLayersOneBased.intValue);
                    volumeDistanceCrossfadeLayer.intValue = volumeDistanceCrossfadeLayerOneBased.intValue - 1;
                    EndChange();

                    // Which layer this is
                    BeginChange();
                    volumeDistanceCrossfadeLayerOneBased.intValue = EditorGUILayout.IntSlider(new GUIContent(EditorTextSoundContainer.volumeDistanceCrossfadeThisIsLabel, EditorTextSoundContainer.volumeDistanceCrossfadeThisIsTooltip), volumeDistanceCrossfadeLayerOneBased.intValue, 1, volumeDistanceCrossfadeTotalLayersOneBased.intValue);
                    volumeDistanceCrossfadeLayerOneBased.intValue = Mathf.Clamp(volumeDistanceCrossfadeLayerOneBased.intValue, 0, volumeDistanceCrossfadeTotalLayersOneBased.intValue);
                    volumeDistanceCrossfadeLayer.intValue = volumeDistanceCrossfadeLayerOneBased.intValue - 1;
                    volumeDistanceCrossfadeLayer.intValue = Mathf.Clamp(volumeDistanceCrossfadeLayer.intValue, 0, volumeDistanceCrossfadeTotalLayers.intValue);
                    EndChange();

                    BeginChange();
                    EditorGUILayout.Slider(volumeDistanceCrossfadeRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.volumeDistanceCrossfadeRolloffLabel, EditorTextSoundContainer.volumeDistanceCrossfadeRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(volumeDistanceCrossfadeCurve, EditorColor.GetVolumeMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.volumeDistanceCrossfadeCurveLabel, EditorTextSoundContainer.volumeDistanceCrossfadeCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.Separator();
            }
        }

        private void GuiVolumeIntensityCrossfade() {

            BeginChange();
            EditorGUILayout.PropertyField(volumeIntensityCrossfadeEnable, new GUIContent(EditorTextSoundContainer.volumeIntensityCrossfadeEnabledLabel, EditorTextSoundContainer.volumeIntensityCrossfadeEnabledTooltip));
            EndChange();

            if (volumeIntensityCrossfadeEnable.boolValue) {

                EditorGUI.indentLevel++;
                // Total Number of Layers 
                BeginChange();
                EditorGUILayout.PropertyField(volumeIntensityCrossfadeTotalLayersOneBased, new GUIContent(EditorTextSoundContainer.volumeIntensityCrossfadeLayersLabel, EditorTextSoundContainer.volumeIntensityCrossfadeLayersTooltip));
                volumeIntensityCrossfadeTotalLayersOneBased.intValue = Mathf.Clamp(volumeIntensityCrossfadeTotalLayersOneBased.intValue, 2, int.MaxValue);
                volumeIntensityCrossfadeTotalLayers.intValue = volumeIntensityCrossfadeTotalLayersOneBased.intValue - 1;
                volumeIntensityCrossfadeTotalLayers.intValue = Mathf.Clamp(volumeIntensityCrossfadeTotalLayers.intValue, 1, int.MaxValue);
                // So that the current layer is not more than total number of layers
                volumeIntensityCrossfadeLayerOneBased.intValue = Mathf.Clamp(volumeIntensityCrossfadeLayerOneBased.intValue, 0, volumeIntensityCrossfadeTotalLayersOneBased.intValue);
                volumeIntensityCrossfadeLayer.intValue = volumeIntensityCrossfadeLayerOneBased.intValue - 1;
                EndChange();

                // Which layer this is
                BeginChange();
                volumeIntensityCrossfadeLayerOneBased.intValue = EditorGUILayout.IntSlider(new GUIContent(EditorTextSoundContainer.volumeIntensityCrossfadeThisIsLabel, EditorTextSoundContainer.volumeIntensityCrossfadeThisIsTooltip), volumeIntensityCrossfadeLayerOneBased.intValue, 1, volumeIntensityCrossfadeTotalLayersOneBased.intValue);
                volumeIntensityCrossfadeLayerOneBased.intValue = Mathf.Clamp(volumeIntensityCrossfadeLayerOneBased.intValue, 0, volumeIntensityCrossfadeTotalLayersOneBased.intValue);
                volumeIntensityCrossfadeLayer.intValue = volumeIntensityCrossfadeLayerOneBased.intValue - 1;
                volumeIntensityCrossfadeLayer.intValue = Mathf.Clamp(volumeIntensityCrossfadeLayer.intValue, 0, volumeIntensityCrossfadeTotalLayers.intValue);
                EndChange();

                BeginChange();
                EditorGUILayout.Slider(volumeIntensityCrossfadeRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.volumeIntensityCrossfadeRolloffLabel, EditorTextSoundContainer.volumeIntensityCrossfadeRolloffTooltip));
                EndChange();

                BeginChange();
                EditorGUILayout.CurveField(volumeIntensityCrossfadeCurve, EditorColor.GetVolumeMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.intensityCrossfadeCurveLabel, EditorTextSoundContainer.intensityCrossfadeCurveTooltip), GUILayout.Height(guiCurveHeight));
                EndChange();
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.Separator();
        }

        private void GuiPitch() {

            // Pitch
            StartBackgroundColor(EditorColor.GetPitchMax(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            BeginChange();
            EditorGuiFunction.DrawFoldout(pitchExpand, "Pitch");
            EndChange();

            if (pitchExpand.boolValue) {
                BeginChange();
                EditorGUILayout.Slider(pitchSemitoneEditor, -24f, 24f, new GUIContent(EditorTextSoundContainer.pitchLabel, EditorTextSoundContainer.pitchTooltip));
                if (pitchRatio.floatValue != PitchScale.SemitonesToRatio(pitchSemitoneEditor.floatValue)) {
                    pitchRatio.floatValue = PitchScale.SemitonesToRatio(pitchSemitoneEditor.floatValue);
                }
                EndChange();

                BeginChange();
                EditorGUILayout.PropertyField(pitchRandomEnable, new GUIContent(EditorTextSoundContainer.pitchRandomLabel, EditorTextSoundContainer.pitchRandomTooltip));
                EndChange();

                if (pitchRandomEnable.boolValue) {
                    EditorGUI.indentLevel++;
                    BeginChange();
                    EditorGUILayout.Slider(pitchRandomRangeSemitone, 0f, 24f, new GUIContent(EditorTextSoundContainer.pitchRandomRangeLabel, EditorTextSoundContainer.pitchRandomRangeTooltip));
                    EndChange();
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.Separator();

                // Pitch Intensity
                BeginChange();
                EditorGUILayout.PropertyField(pitchIntensityEnable, new GUIContent(EditorTextSoundContainer.pitchIntensityEnableLabel, EditorTextSoundContainer.pitchIntensityEnableTooltip));
                EndChange();

                if (pitchIntensityEnable.boolValue) {
                    EditorGUI.indentLevel++;

                    // Pitch Highest
                    BeginChange();
                    EditorGUILayout.PropertyField(pitchIntensityHighSemitone, new GUIContent(EditorTextSoundContainer.pitchIntensityRangeLabel, EditorTextSoundContainer.pitchIntensityRangeTooltip));
                    pitchIntensityHighSemitone.floatValue = Mathf.Clamp(pitchIntensityHighSemitone.floatValue, -128, 128);
                    if (pitchIntensityHighRatio.floatValue != PitchScale.SemitonesToRatio(pitchIntensityHighSemitone.floatValue)) {
                        pitchIntensityHighRatio.floatValue = PitchScale.SemitonesToRatio(pitchIntensityHighSemitone.floatValue);
                        // Converting from high/low to base/range
                        pitchIntensityBaseSemitone.floatValue = pitchIntensityLowSemitone.floatValue;
                        pitchIntensityBaseRatio.floatValue = PitchScale.SemitonesToRatio(pitchIntensityBaseSemitone.floatValue);
                        pitchIntensityRangeSemitone.floatValue = -(pitchIntensityLowSemitone.floatValue - pitchIntensityHighSemitone.floatValue);
                        pitchIntensityRangeRatio.floatValue = PitchScale.SemitonesToRatio(pitchIntensityRangeSemitone.floatValue);
                    }
                    EndChange();

                    // Pitch Lowest
                    BeginChange();
                    EditorGUILayout.PropertyField(pitchIntensityLowSemitone, new GUIContent(EditorTextSoundContainer.pitchIntensityBaseLabel, EditorTextSoundContainer.pitchIntensityBaseTooltip));
                    pitchIntensityLowSemitone.floatValue = Mathf.Clamp(pitchIntensityLowSemitone.floatValue, -128, 128);
                    if (pitchIntensityLowRatio.floatValue != PitchScale.SemitonesToRatio(pitchIntensityLowSemitone.floatValue)) {
                        pitchIntensityLowRatio.floatValue = PitchScale.SemitonesToRatio(pitchIntensityLowSemitone.floatValue);
                        // Converting from high/low to base/range
                        pitchIntensityBaseSemitone.floatValue = pitchIntensityLowSemitone.floatValue;
                        pitchIntensityBaseRatio.floatValue = PitchScale.SemitonesToRatio(pitchIntensityBaseSemitone.floatValue);
                        pitchIntensityRangeSemitone.floatValue = -(pitchIntensityLowSemitone.floatValue - pitchIntensityHighSemitone.floatValue);
                        pitchIntensityRangeRatio.floatValue = PitchScale.SemitonesToRatio(pitchIntensityRangeSemitone.floatValue);
                    }
                    EndChange();

                    // Pitch Intensity Rolloff
                    BeginChange();
                    EditorGUILayout.Slider(pitchIntensityRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.pitchIntensityRolloffLabel, EditorTextSoundContainer.pitchIntensityRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(pitchIntensityCurve, EditorColor.GetVolumeMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.pitchIntensityCurveLabel, EditorTextSoundContainer.pitchIntensityCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();
                    EditorGUI.indentLevel--;

                    curveDraw.Draw(EditorSoundContainerCurveType.Pitch, EditorSoundContainerCurveValue.Intensity);
                }
            }

            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        private void GuiSpatialBlend() {
            // Spatial Blend
            StartBackgroundColor(EditorColor.GetSpatialBlendMax(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            BeginChange();
            EditorGuiFunction.DrawFoldout(spatialBlendExpand, "Spatial Blend");
            EndChange();

            if (spatialBlendExpand.boolValue) {

                BeginChange();
                EditorGUILayout.Slider(spatialBlend, 0f, 1f, new GUIContent(EditorTextSoundContainer.spatialBlendBaseLabel, EditorTextSoundContainer.spatialBlendBaseTooltip));
                EndChange();

                if (distanceEnabled.boolValue) {
                    EditorGUILayout.Separator();
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatialBlendDistanceLabel, EditorTextSoundContainer.spatialBlendDistanceTooltip));

                    EditorGUI.indentLevel++;
                    BeginChange();
                    EditorGUILayout.Slider(spatialBlendDistanceRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.spatialBlendDistanceRolloffLabel, EditorTextSoundContainer.spatialBlendDistanceRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.Slider(spatialBlendDistance3DIncrease, 0f, 1f, new GUIContent(EditorTextSoundContainer.spatialBlendDistance3DIncreaseLabel, EditorTextSoundContainer.spatialBlendDistance3DIncreaseTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(spatialBlendDistanceCurve, EditorColor.GetSpatialBlendMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.spatialBlendDistanceCurveLabel, EditorTextSoundContainer.spatialBlendDistanceCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();
                    EditorGUI.indentLevel--;

                    // Preview Curve
                    curveDraw.Draw(EditorSoundContainerCurveType.SpatialBlend, EditorSoundContainerCurveValue.Distance);
                } 

                EditorGUILayout.Separator();
                BeginChange();
                EditorGUILayout.PropertyField(spatialBlendIntensityEnable, new GUIContent(EditorTextSoundContainer.spatialBlendIntensityEnableLabel, EditorTextSoundContainer.spatialBlendIntensityEnableTooltip));
                EndChange();

                if (spatialBlendIntensityEnable.boolValue) {

                    EditorGUI.indentLevel++;
                    BeginChange();
                    EditorGUILayout.Slider(spatialBlendIntensityRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.spatialBlendIntensityRolloffLabel, EditorTextSoundContainer.spatialBlendIntensityRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.Slider(spatialBlendIntensityStrength, 0f, 1f, new GUIContent(EditorTextSoundContainer.spatialBlendIntensityStrengthLabel, EditorTextSoundContainer.spatialBlendIntensityStrengthTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(spatialBlendIntensityCurve, EditorColor.GetSpatialBlendMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.spatialBlendIntensityCurveLabel, EditorTextSoundContainer.spatialBlendIntensityCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();
                    EditorGUI.indentLevel--;

                    // Preview Curve
                    curveDraw.Draw(EditorSoundContainerCurveType.SpatialBlend, EditorSoundContainerCurveValue.Intensity);
                }
            }
            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        private void GuiSpatialSpread() {
            // Spatial Spread
            StartBackgroundColor(EditorColor.GetSpatialSpreadMax(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            BeginChange();
            EditorGuiFunction.DrawFoldout(spatialSpreadExpand, "Spatial Spread");
            EndChange();

            if (spatialSpreadExpand.boolValue) {

                BeginChange();
                EditorGUILayout.Slider(spatialSpreadDegrees, 0f, 360f, new GUIContent(EditorTextSoundContainer.spatialSpreadBaseLabel, EditorTextSoundContainer.spatialSpreadBaseTooltip));
                if (spatialSpreadRatio.floatValue != spatialSpreadDegrees.floatValue / 360f) {
                    spatialSpreadRatio.floatValue = spatialSpreadDegrees.floatValue / 360f;
                }
                EndChange();

                if (distanceEnabled.boolValue) {
                    EditorGUILayout.Separator();
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.spatialSpreadDistanceLabel, EditorTextSoundContainer.spatialSpreadDistanceTooltip));

                    EditorGUI.indentLevel++;
                    BeginChange();
                    EditorGUILayout.Slider(spatialSpreadDistanceRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.spatialSpreadDistanceRolloffLabel, EditorTextSoundContainer.spatialSpreadDistanceRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(spatialSpreadDistanceCurve, EditorColor.GetSpatialSpreadMax(1), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.spatialSpreadDistanceCurveLabel, EditorTextSoundContainer.spatialSpreadDistanceCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();
                    EditorGUI.indentLevel--;

                    // Preview Curve
                    curveDraw.Draw(EditorSoundContainerCurveType.SpatialSpread, EditorSoundContainerCurveValue.Distance);
                } 
                EditorGUILayout.Separator();

                BeginChange();
                EditorGUILayout.PropertyField(spatialSpreadIntensityEnable, new GUIContent(EditorTextSoundContainer.spatialSpreadIntensityEnableLabel, EditorTextSoundContainer.spatialSpreadIntensityEnableTooltip));
                EndChange();

                if (spatialSpreadIntensityEnable.boolValue) {
                    EditorGUI.indentLevel++;

                    BeginChange();
                    EditorGUILayout.Slider(spatialSpreadIntensityRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.spatialSpreadIntensityRolloffLabel, EditorTextSoundContainer.spatialSpreadIntensityRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.Slider(spatialSpreadIntensityStrength, 0f, 1f, new GUIContent(EditorTextSoundContainer.spatialSpreadIntensityStrengthLabel, EditorTextSoundContainer.spatialSpreadIntensityStrengthTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(spatialSpreadIntensityCurve, EditorColor.GetSpatialSpreadMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.spatialSpreadIntensityCurveLabel, EditorTextSoundContainer.spatialSpreadIntensityCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();
                    EditorGUI.indentLevel--;

                    // Preview Curve
                    curveDraw.Draw(EditorSoundContainerCurveType.SpatialSpread, EditorSoundContainerCurveValue.Intensity);
                }
            }
            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        private void GuiStereoPan() {

            // StereoPan
            StartBackgroundColor(EditorColor.GetStereoPanMax(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            BeginChange();
            EditorGuiFunction.DrawFoldout(stereoPanExpand, "Stereo Pan");
            EndChange();

            if (stereoPanExpand.boolValue) {
                // StereoPan Offset
                BeginChange();
                EditorGUILayout.Slider(stereoPanOffset, -1f, 1f, new GUIContent(EditorTextSoundContainer.stereoPanOffsetLabel, EditorTextSoundContainer.stereoPanOffsetTooltip));
                EndChange();

                // StereoPan Angle Use
                BeginChange();
                stereoPanAngleUse.boolValue = EditorGUILayout.Toggle(new GUIContent(EditorTextSoundContainer.stereoPanAngleToSteroPanUseLabel, EditorTextSoundContainer.stereoPanAngleToSteroPanUseTooltip), stereoPanAngleUse.boolValue);
                EndChange();

                if (stereoPanAngleUse.boolValue) {
                    EditorGUI.indentLevel++;

                    // Stereo Pan Automatic Angle Amount
                    BeginChange();
                    EditorGUILayout.Slider(stereoPanAngleAmount, 0f, 1f, new GUIContent(EditorTextSoundContainer.stereoPanAngleToSteroPanStrengthLabel, EditorTextSoundContainer.stereoPanAngleToSteroPanStrengthTooltip));
                    EndChange();

                    // Stereo Pan Angle Rolloff
                    BeginChange();
                    EditorGUILayout.Slider(stereoPanAngleRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.stereoPanAngleToSteroPanRolloffLabel, EditorTextSoundContainer.stereoPanAngleToSteroPanRolloffTooltip));
                    EndChange();

                    curveDraw.Draw(EditorSoundContainerCurveType.StereoPan, EditorSoundContainerCurveValue.Angle);

                    EditorGUI.indentLevel--;
                }
            }

            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        private void GuiReverbZoneMix() {

            // ReverbZoneMix
            StartBackgroundColor(EditorColor.GetReverbZoneMixColorMax(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            BeginChange();
            EditorGuiFunction.DrawFoldout(reverbZoneMixExpand, "Reverb Zone Mix");
            EndChange();

            if (reverbZoneMixExpand.boolValue) {

                // Reverb Zone Mix
                BeginChange();
                EditorGUILayout.Slider(reverbZoneMixDecibel, VolumeScale.lowestReverbMixDecibel, 10f, new GUIContent(EditorTextSoundContainer.reverbZoneMixDecibelLabel, EditorTextSoundContainer.reverbZoneMixDecibelTooltip));
                if (reverbZoneMixDecibel.floatValue <= VolumeScale.lowestReverbMixDecibel) {
                    reverbZoneMixDecibel.floatValue = Mathf.NegativeInfinity;
                }
                // Max value is +10 dB (3.1622776601683795)
                // Will be scaled later to where 1.1 is +10dB
                if (reverbZoneMixRatio.floatValue != VolumeScale.ConvertDecibelToRatio(reverbZoneMixDecibel.floatValue)) {
                    reverbZoneMixRatio.floatValue = VolumeScale.ConvertDecibelToRatio(reverbZoneMixDecibel.floatValue);
                }
                EndChange();

                if (distanceEnabled.boolValue) {
                    EditorGUILayout.Separator();
                    EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.reverbZoneMixDistanceLabel, EditorTextSoundContainer.reverbZoneMixDistanceTooltip));

                    EditorGUI.indentLevel++;
                    BeginChange();
                    EditorGUILayout.Slider(reverbZoneMixDistanceRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.reverbZoneMixDistanceRolloffLabel, EditorTextSoundContainer.reverbZoneMixDistanceRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.Slider(reverbZoneMixDistanceIncrease, 0f, 1f, new GUIContent(EditorTextSoundContainer.reverbZoneMixDistanceIncreaseLabel, EditorTextSoundContainer.reverbZoneMixDistanceIncreaseTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(reverbZoneMixDistanceCurve, EditorColor.GetReverbZoneMixColorMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.reverbZoneMixDistanceCurveLabel, EditorTextSoundContainer.reverbZoneMixDistanceCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();
                    EditorGUI.indentLevel--;

                    EditorGUILayout.Separator();

                    // Preview Curve
                    curveDraw.Draw(EditorSoundContainerCurveType.ReverbZoneMix, EditorSoundContainerCurveValue.Distance);
                } 
                EditorGUILayout.Separator();

                BeginChange();
                EditorGUILayout.PropertyField(reverbZoneMixIntensityEnable, new GUIContent(EditorTextSoundContainer.reverbZoneMixIntensityEnableLabel, EditorTextSoundContainer.reverbZoneMixIntensityEnableTooltip));
                EndChange();

                if (reverbZoneMixIntensityEnable.boolValue) {

                    EditorGUI.indentLevel++;

                    BeginChange();
                    EditorGUILayout.Slider(reverbZoneMixIntensityRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.reverbZoneMixIntensityRolloffLabel, EditorTextSoundContainer.reverbZoneMixIntensityRolloffTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.Slider(reverbZoneMixIntensityAmount, 0f, 1f, new GUIContent(EditorTextSoundContainer.reverbZoneMixIntensityStrengthLabel, EditorTextSoundContainer.reverbZoneMixIntensityStrengthTooltip));
                    EndChange();

                    BeginChange();
                    EditorGUILayout.CurveField(reverbZoneMixIntensityCurve, EditorColor.GetReverbZoneMixColorMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.reverbZoneMixIntensityCurveLabel, EditorTextSoundContainer.reverbZoneMixIntensityCurveTooltip), GUILayout.Height(guiCurveHeight));
                    EndChange();

                    EditorGUI.indentLevel--;

                    EditorGUILayout.Separator();

                    curveDraw.Draw(EditorSoundContainerCurveType.ReverbZoneMix, EditorSoundContainerCurveValue.Intensity);
                }
            }

            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        private void GuiDistortion() {

            // Distortion
            StartBackgroundColor(EditorColor.GetDistortionMax(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            BeginChange();
            EditorGuiFunction.DrawFoldout(distortionExpand, "Distortion");
            EndChange();

            if (distortionExpand.boolValue) {

                BeginChange();
                EditorGUILayout.PropertyField(distortionEnabled, new GUIContent(EditorTextSoundContainer.distortionEnableLabel, EditorTextSoundContainer.distortionEnableTooltip));
                EndChange();

                if (distortionEnabled.boolValue) {
                
                    BeginChange();
                    EditorGUILayout.Slider(distortionAmount, 0f, 1f, new GUIContent(EditorTextSoundContainer.distortionAmountLabel, EditorTextSoundContainer.distortionAmountTooltip));
                    EndChange();

                    if (distanceEnabled.boolValue) {

                        EditorGUILayout.Separator();
                        BeginChange();
                        EditorGUILayout.PropertyField(distortionDistanceEnable, new GUIContent(EditorTextSoundContainer.distortionDistanceLabel, EditorTextSoundContainer.distortionDistanceTooltip));
                        EndChange();

                        if (distortionDistanceEnable.boolValue) {
                            EditorGUI.indentLevel++;
                            BeginChange();
                            EditorGUILayout.Slider(distortionDistanceRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.distortionDistanceRolloffLabel, EditorTextSoundContainer.distortionDistanceRolloffTooltip));
                            EndChange();

                            BeginChange();
                            EditorGUILayout.CurveField(distortionDistanceCurve, EditorColor.GetDistortionMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.distortionDistanceCurveLabel, EditorTextSoundContainer.distortionDistanceCurveTooltip), GUILayout.Height(guiCurveHeight));
                            EndChange();
                            EditorGUI.indentLevel--;

                            // Preview Curve
                            curveDraw.Draw(EditorSoundContainerCurveType.Distortion, EditorSoundContainerCurveValue.Distance);
                        }
                    } 
                    EditorGUILayout.Separator();

                    BeginChange();
                    EditorGUILayout.PropertyField(distortionIntensityEnable, new GUIContent(EditorTextSoundContainer.distortionIntensityEnableLabel, EditorTextSoundContainer.distortionIntensityEnableTooltip));
                    EndChange();

                    if (distortionIntensityEnable.boolValue) {

                        EditorGUI.indentLevel++;
                        BeginChange();
                        EditorGUILayout.Slider(distortionIntensityRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.distortionIntensityRolloffLabel, EditorTextSoundContainer.distortionIntensityRolloffTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.Slider(distortionIntensityStrength, 0f, 1f, new GUIContent(EditorTextSoundContainer.distortionIntensityStrengthLabel, EditorTextSoundContainer.distortionIntensityStrengthTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.CurveField(distortionIntensityCurve, EditorColor.GetDistortionMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.distortionIntensityCurveLabel, EditorTextSoundContainer.distortionIntensityCurveTooltip), GUILayout.Height(guiCurveHeight));
                        EndChange();
                        EditorGUI.indentLevel--;

                        // Preview Curve
                        curveDraw.Draw(EditorSoundContainerCurveType.Distortion, EditorSoundContainerCurveValue.Intensity);
                    }
                }
            }
            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        private void GuiLowpass() {

            // Lowpass
            StartBackgroundColor(EditorColor.GetLowpassAmountMax(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            BeginChange();
            EditorGuiFunction.DrawFoldout(lowpassExpand, "Lowpass Filter");
            EndChange();

            if (lowpassExpand.boolValue) {

                BeginChange();
                EditorGUILayout.PropertyField(lowpassEnabled, new GUIContent(EditorTextSoundContainer.lowpassEnableLabel, EditorTextSoundContainer.lowpassEnableTooltip));
                EndChange();

                if (lowpassEnabled.boolValue) {

                    BeginChange();
                    EditorGUILayout.Slider(lowpassFrequencyEditor, 20f, 20000f, new GUIContent(EditorTextSoundContainer.lowpassFrequencyLabel, EditorTextSoundContainer.lowpassFrequencyTooltip));
                    // Convert values for engine
                    lowpassFrequencyEngine.floatValue = (lowpassFrequencyEditor.floatValue - 20f) / 19980f;
                    EndChange();

                    BeginChange();
                    EditorGUILayout.Slider(lowpassAmountEditor, 0f, 6f, new GUIContent(EditorTextSoundContainer.lowpassAmountLabel, EditorTextSoundContainer.lowpassAmountTooltip));
                    // Convert values for engine
                    lowpassAmountEngine.floatValue = lowpassAmountEditor.floatValue / 6f;
                    EndChange();

                    if (distanceEnabled.boolValue) {

                        EditorGUILayout.Separator();
                        BeginChange();
                        EditorGUILayout.PropertyField(lowpassDistanceEnable, new GUIContent(EditorTextSoundContainer.lowpassDistanceLabel, EditorTextSoundContainer.lowpassDistanceTooltip));
                        EndChange();
                        
                        if (lowpassDistanceEnable.boolValue) {

                            EditorGUI.indentLevel++;
                            BeginChange();
                            EditorGUILayout.Slider(lowpassDistanceFrequencyRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.lowpassDistanceFrequencyRolloffLabel, EditorTextSoundContainer.lowpassDistanceFrequencyRolloffTooltip));
                            EndChange();

                            BeginChange();
                            EditorGUILayout.CurveField(lowpassDistanceFrequencyCurve, EditorColor.GetLowpassFrequencyMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.lowpassDistanceFrequencyCurveLabel, EditorTextSoundContainer.lowpassDistanceFrequencyCurveTooltip), GUILayout.Height(guiCurveHeight));
                            EndChange();
                            EditorGUI.indentLevel--;

                            // Preview Curve
                            curveDraw.Draw(EditorSoundContainerCurveType.LowpassFrequency, EditorSoundContainerCurveValue.Distance);
                            EditorGUILayout.Separator();

                            EditorGUI.indentLevel++;
                            BeginChange();
                            EditorGUILayout.Slider(lowpassDistanceAmountRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.lowpassDistanceAmountRolloffLabel, EditorTextSoundContainer.lowpassDistanceAmountRolloffTooltip));
                            EndChange();

                            BeginChange();
                            EditorGUILayout.CurveField(lowpassDistanceAmountCurve, EditorColor.GetLowpassAmountMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.lowpassDistanceAmountCurveLabel, EditorTextSoundContainer.lowpassDistanceAmountCurveTooltip), GUILayout.Height(guiCurveHeight));
                            EndChange();
                            EditorGUI.indentLevel--;

                            // Preview Curve
                            curveDraw.Draw(EditorSoundContainerCurveType.LowpassAmount, EditorSoundContainerCurveValue.Distance);
                        }
                    } 

                    EditorGUILayout.Separator();
                    BeginChange();
                    EditorGUILayout.PropertyField(lowpassIntensityEnable, new GUIContent(EditorTextSoundContainer.lowpassIntensityEnableLabel, EditorTextSoundContainer.lowpassIntensityEnableTooltip));
                    EndChange();

                    if (lowpassIntensityEnable.boolValue) {
                        EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.lowpassIntensityFrequencyLabel, EditorTextSoundContainer.lowpassIntensityFrequencyTooltip));

                        EditorGUI.indentLevel++;
                        BeginChange();
                        EditorGUILayout.Slider(lowpassIntensityFrequencyRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.lowpassIntensityFrequencyRolloffLabel, EditorTextSoundContainer.lowpassIntensityFrequencyRolloffTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.Slider(lowpassIntensityFrequencyStrength, 0f, 1f, new GUIContent(EditorTextSoundContainer.lowpassIntensityFrequencyStrengthLabel, EditorTextSoundContainer.lowpassIntensityFrequencyStrengthTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.CurveField(lowpassIntensityFrequencyCurve, EditorColor.GetLowpassFrequencyMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.lowpassIntensityFrequencyCurveLabel, EditorTextSoundContainer.lowpassIntensityFrequencyCurveTooltip), GUILayout.Height(guiCurveHeight));
                        EndChange();
                        EditorGUI.indentLevel--;

                        // Preview Curve
                        curveDraw.Draw(EditorSoundContainerCurveType.LowpassFrequency, EditorSoundContainerCurveValue.Intensity);

                        EditorGUILayout.Separator();

                        EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.lowpassIntensityAmountLabel, EditorTextSoundContainer.lowpassIntensityAmountTooltip));

                        EditorGUI.indentLevel++;
                        BeginChange();
                        EditorGUILayout.Slider(lowpassIntensityAmountRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.lowpassIntensityAmountRolloffLabel, EditorTextSoundContainer.lowpassIntensityAmountRolloffTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.Slider(lowpassIntensityAmountStrength, 0f, 1f, new GUIContent(EditorTextSoundContainer.lowpassIntensityAmountStrengthLabel, EditorTextSoundContainer.lowpassIntensityAmountStrengthTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.CurveField(lowpassIntensityAmountCurve, EditorColor.GetLowpassAmountMax(1f), new Rect(0f, 0f, 1f, 1f),  new GUIContent(EditorTextSoundContainer.lowpassIntensityAmountCurveLabel, EditorTextSoundContainer.lowpassIntensityAmountCurveTooltip), GUILayout.Height(guiCurveHeight));
                        EndChange();
                        EditorGUI.indentLevel--;

                        // Preview Curve
                        curveDraw.Draw(EditorSoundContainerCurveType.LowpassAmount, EditorSoundContainerCurveValue.Intensity);
                    }
                }
            }
            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        private void GuiHighpass() {

            // Highpass
            StartBackgroundColor(EditorColor.GetHighpassAmountMax(EditorColorProSkin.GetCustomEditorBackgroundAlpha()));

            BeginChange();
            EditorGuiFunction.DrawFoldout(highpassExpand, "Highpass Filter");
            EndChange();

            if (highpassExpand.boolValue) {

                BeginChange();
                EditorGUILayout.PropertyField(highpassEnabled, new GUIContent(EditorTextSoundContainer.highpassEnableLabel, EditorTextSoundContainer.highpassEnableTooltip));
                EndChange();

                if (highpassEnabled.boolValue) {

                    BeginChange();
                    EditorGUILayout.Slider(highpassFrequencyEditor, 20f, 20000f, new GUIContent(EditorTextSoundContainer.highpassFrequencyLabel, EditorTextSoundContainer.highpassFrequencyTooltip));
                    // Convert values for engine
                    highpassFrequencyEngine.floatValue = (highpassFrequencyEditor.floatValue - 20f) / 19980f;
                    EndChange();

                    BeginChange();
                    EditorGUILayout.Slider(highpassAmountEditor, 0f, 6f, new GUIContent(EditorTextSoundContainer.highpassAmountLabel, EditorTextSoundContainer.highpassAmountTooltip));
                    // Convert values for engine
                    highpassAmountEngine.floatValue = highpassAmountEditor.floatValue / 6f;
                    EndChange();

                    if (distanceEnabled.boolValue) {

                        EditorGUILayout.Separator();
                        BeginChange();
                        EditorGUILayout.PropertyField(highpassDistanceEnable, new GUIContent(EditorTextSoundContainer.highpassDistanceLabel, EditorTextSoundContainer.highpassDistanceTooltip));
                        EndChange();

                        if (highpassDistanceEnable.boolValue) {
                            EditorGUI.indentLevel++;
                            BeginChange();
                            EditorGUILayout.Slider(highpassDistanceFrequencyRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.highpassDistanceFrequencyRolloffLabel, EditorTextSoundContainer.highpassDistanceFrequencyRolloffTooltip));
                            EndChange();

                            BeginChange();
                            EditorGUILayout.CurveField(highpassDistanceFrequencyCurve, EditorColor.GetHighpassFrequencyMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.highpassDistanceFrequencyCurveLabel, EditorTextSoundContainer.highpassDistanceFrequencyCurveTooltip), GUILayout.Height(guiCurveHeight));
                            EndChange();
                            EditorGUI.indentLevel--;

                            // Preview Curve
                            curveDraw.Draw(EditorSoundContainerCurveType.HighpassFrequency, EditorSoundContainerCurveValue.Distance);
                            EditorGUILayout.Separator();

                            EditorGUI.indentLevel++;
                            BeginChange();
                            EditorGUILayout.Slider(highpassDistanceAmountRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.highpassDistanceAmountRolloffLabel, EditorTextSoundContainer.highpassDistanceAmountRolloffTooltip));
                            EndChange();

                            BeginChange();
                            EditorGUILayout.CurveField(highpassDistanceAmountCurve, EditorColor.GetHighpassAmountMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.highpassDistanceAmountCurveLabel, EditorTextSoundContainer.highpassDistanceAmountCurveTooltip), GUILayout.Height(guiCurveHeight));
                            EndChange();
                            EditorGUI.indentLevel--;

                            // Preview Curve
                            curveDraw.Draw(EditorSoundContainerCurveType.HighpassAmount, EditorSoundContainerCurveValue.Distance);
                        }
                    }

                    EditorGUILayout.Separator();
                    BeginChange();
                    EditorGUILayout.PropertyField(highpassIntensityEnable, new GUIContent(EditorTextSoundContainer.highpassIntensityEnableLabel, EditorTextSoundContainer.highpassIntensityEnableTooltip));
                    EndChange();

                    if (highpassIntensityEnable.boolValue) {
                        EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.highpassIntensityFrequencyLabel, EditorTextSoundContainer.highpassIntensityFrequencyTooltip));

                        EditorGUI.indentLevel++;
                        BeginChange();
                        EditorGUILayout.Slider(highpassIntensityFrequencyRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.highpassIntensityFrequencyRolloffLabel, EditorTextSoundContainer.highpassIntensityFrequencyRolloffTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.Slider(highpassIntensityFrequencyStrength, 0f, 1f, new GUIContent(EditorTextSoundContainer.highpassIntensityFrequencyStrengthLabel, EditorTextSoundContainer.highpassIntensityFrequencyStrengthTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.CurveField(highpassIntensityFrequencyCurve, EditorColor.GetHighpassFrequencyMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.highpassIntensityFrequencyCurveLabel, EditorTextSoundContainer.highpassIntensityFrequencyCurveTooltip), GUILayout.Height(guiCurveHeight));
                        EndChange();
                        EditorGUI.indentLevel--;

                        // Preview Curve
                        curveDraw.Draw(EditorSoundContainerCurveType.HighpassFrequency, EditorSoundContainerCurveValue.Intensity);

                        EditorGUILayout.Separator();
                        EditorGUILayout.LabelField(new GUIContent(EditorTextSoundContainer.highpassIntensityAmountLabel, EditorTextSoundContainer.highpassIntensityAmountTooltip));

                        EditorGUI.indentLevel++;
                        BeginChange();
                        EditorGUILayout.Slider(highpassIntensityAmountRolloff, -LogLinExp.bipolarRange, LogLinExp.bipolarRange, new GUIContent(EditorTextSoundContainer.highpassIntensityAmountRolloffLabel, EditorTextSoundContainer.highpassIntensityAmountRolloffTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.Slider(highpassIntensityAmountStrength, 0f, 1f, new GUIContent(EditorTextSoundContainer.highpassIntensityAmountStrengthLabel, EditorTextSoundContainer.highpassIntensityAmountStrengthTooltip));
                        EndChange();

                        BeginChange();
                        EditorGUILayout.CurveField(highpassIntensityAmountCurve, EditorColor.GetHighpassAmountMax(1f), new Rect(0f, 0f, 1f, 1f), new GUIContent(EditorTextSoundContainer.highpassIntensityAmountCurveLabel, EditorTextSoundContainer.highpassIntensityAmountCurveTooltip), GUILayout.Height(guiCurveHeight));
                        EndChange();
                        EditorGUI.indentLevel--;

                        // Preview Curve
                        curveDraw.Draw(EditorSoundContainerCurveType.HighpassAmount, EditorSoundContainerCurveValue.Intensity);
                    }
                }
            }
            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

        private void GuiReset() {

            // Transparent background so the offset will be right
            StartBackgroundColor(new Color(0f, 0f, 0f, 0f));
            BeginChange();
            EditorGUILayout.PropertyField(previewCurves, new GUIContent(EditorTextSoundContainer.showPreviewCurvesLabel, EditorTextSoundContainer.showPreviewCurvesTooltip));
            EndChange();
            StopBackgroundColor();

            // Transparent background so the offset will be right
            StartBackgroundColor(new Color(0f, 0f, 0f, 0f));
            EditorGUILayout.BeginHorizontal();
            // For offsetting the buttons to the right
            EditorGUILayout.LabelField(new GUIContent(""), GUILayout.Width(EditorGUIUtility.labelWidth));

            // Reset
            BeginChange();
            if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.resetSettingsLabel, EditorTextSoundContainer.resetSettingsTooltip))) {
                for (int i = 0; i < mTargets.Length; i++) {
                    Undo.RecordObject(mTargets[i], "Reset Settings");
                    mTargets[i].internals.data = new SoundContainerInternalsData();
                    EditorUtility.SetDirty(mTargets[i]);
                }
            }
            EndChange();
            BeginChange();
            if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.resetAllLabel, EditorTextSoundContainer.resetAllTooltip))) {
                for (int i = 0; i < mTargets.Length; i++) {
                    Undo.RecordObject(mTargets[i], "Reset All");
                    mTargets[i].internals.data = new SoundContainerInternalsData();
                    mTargets[i].internals.audioClips = new AudioClip[1];
                    mTargets[i].internals.notes = "Notes";
                    EditorUtility.SetDirty(mTargets[i]);
                }
            }
            EndChange();
            EditorGUILayout.EndHorizontal();
            StopBackgroundColor();
        }

        private void GuiFindReferences() {

            EditorGUI.indentLevel = 0;
            StartBackgroundColor(Color.grey);
            EditorGUILayout.BeginHorizontal();
            if (mTarget.internals.data.foundReferences == null || mTarget.internals.data.foundReferences.Length == 0) {
                EditorGUILayout.LabelField(new GUIContent("No Search"), GUILayout.Width(EditorGUIUtility.labelWidth));
            } else {
                EditorGUILayout.LabelField(new GUIContent(mTarget.internals.data.foundReferences.Length + " References Found"), GUILayout.Width(EditorGUIUtility.labelWidth));
            }
            BeginChange();
            if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.findReferencesLabel, EditorTextSoundContainer.findReferencesTooltip))) {
                for (int i = 0; i < mTargets.Length; i++) {
                    mTargets[i].internals.data.foundReferences = EditorFindReferences.GetObjects(mTargets[i]);
                    EditorUtility.SetDirty(mTargets[i]);
                }
                GUIUtility.ExitGUI();
            }
            EndChange();
            BeginChange();
            if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.findReferencesSelectAllLabel, EditorTextSoundContainer.findReferencesSelectAllTooltip))) {
                List<UnityEngine.Object> newSelection = new List<UnityEngine.Object>();
                for (int i = 0; i < mTargets.Length; i++) {
                    if (mTargets[i].internals.data.foundReferences != null) {
                        newSelection.AddRange(mTargets[i].internals.data.foundReferences);
                    }
                }
                // Only select if something is found
                if (newSelection != null && newSelection.Count > 0) {
                    Selection.objects = newSelection.ToArray();
                }
            }
            EndChange();
            BeginChange();
            if (GUILayout.Button(new GUIContent(EditorTextSoundContainer.findReferencesClearLabel, EditorTextSoundContainer.findReferencesClearTooltip))) {
                for (int i = 0; i < mTargets.Length; i++) {
                    mTargets[i].internals.data.foundReferences = new UnityEngine.Object[0];
                    EditorUtility.SetDirty(mTargets[i]);
                }
                GUIUtility.ExitGUI();
            }
            EndChange();
            EditorGUILayout.EndHorizontal();

            // Showing the references
            for (int i = 0; i < foundReferences.arraySize; i++) {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(foundReferences.GetArrayElementAtIndex(i), new GUIContent(foundReferences.GetArrayElementAtIndex(i).objectReferenceValue.name));
                EditorGUILayout.EndHorizontal();
            }
            StopBackgroundColor();
            EditorGUILayout.Separator();
        }

    }
}
#endif