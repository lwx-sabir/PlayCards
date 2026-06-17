// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

// Sonity Steam Audio
#if UNITY_EDITOR && SONITY_ENABLE_INTEGRATION_STEAM_AUDIO

namespace Sonity.Internal {

    public class EditorTextSteamAudio {

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

        public static readonly string directBinauralLabel = $"Direct Binaural";
        public static readonly string directBinauralTooltip = $"If checked, HRTF-based binaural rendering will be used to spatialize the source. This requires 2-channel (stereo) audio output. If unchecked, panning will be used to the spatialize the source using the user’s speaker layout. Binaural rendering provides improved spatialization at the cost of slightly increased CPU usage." + EditorTrial.trialTooltip;

        public static readonly string interpolationLabel = $"Interpolation";
        public static readonly string interpolationTooltip =
       $"Controls how HRTFs are interpolated when the source moves relative to the listener." + "\n" +
       "\n" +
       $"Nearest: Uses the HRTF from the direction nearest to the direction of the source for which HRTF data is available. The fastest option, but can result in audible artifacts for certain kinds of audio clips, such as white noise or engine sounds." + "\n" +
       "\n" +
       $"Bilinear: Uses an HRTF generated after interpolating from four directions nearest to the direction of the source, for which HRTF data is available. This may result in smoother audio for some kinds of sources when the listener looks around, but has higher CPU usage (up to 2x)." + EditorTrial.trialTooltip;

        public static readonly string perspectiveCorrectionLabel = $"Perspective Correction";
        public static readonly string perspectiveCorrectionTooltip =
       $"If checked, perspective correction (based on the projection matrix of the current main camera) is applied to this source during spatialization. This can improve the perceived positional accuracy in non-VR applications. See Steam Audio Settings for more details." + "\n" +
       "\n" +
       $"Requires Enable Perspective Correction to be checked in Steam Audio Settings." + EditorTrial.trialTooltip;

        public static readonly string distanceAttenuationLabel = $"Distance Attenuation";
        public static readonly string distanceAttenuationTooltip = $"If checked, distance attenuation will be calculated and applied to the Audio Source. This takes into account the Spatial Blend setting on the Audio Source, so if Spatial Blend is set to 2D, distance attenuation is effectively not applied." + EditorTrial.trialTooltip;

        public static readonly string distanceAttenuationInputLabel = $"Input";
        public static readonly string distanceAttenuationInputTooltip =
       $"Specifies how the distance attenuation value is determined." + "\n" +
       "\n" +
       $"Curve Driven: Distance attenuation is controlled by the Volume curve on the Audio Source." + "\n" +
       "\n" +
       $"Physics Based: A physics-based distance attenuation model is used. This is an inverse distance falloff. The curves defined on the Audio Source are ignored." + EditorTrial.trialTooltip;

        public static readonly string airAbsorptionLabel = $"Air Absorption";
        public static readonly string airAbsorptionTooltip = $"If checked, frequency-dependent distance based air absorption will be calculated and applied to the Audio Source." + EditorTrial.trialTooltip;

        public static readonly string airAbsorptionInputLabel = $"Input";
        public static readonly string airAbsorptionInputTooltip =
       $"Specifies how the air absorption values (which are 3-band EQ values) are determined." + "\n" +
       "\n" +
       $"Simulation Defined: Uses a physics-based air absorption model. This is an exponential falloff, with higher frequencies falling off faster with distance than lower frequencies." + "\n" +
       "\n" +
       $"User Defined: Uses the values specified in the Air Absorption Low, Air Absorption Mid, and Air Absorption High sliders as the EQ values. The air absorption value will not automatically change with distance to the source. You are expected to control the Air Absorption Low, Air Absorption Mid, and Air Absorption High sliders using a custom script to achieve this effect." + EditorTrial.trialTooltip;

        public static readonly string airAbsorptionLowLabel = $"Low";
        public static readonly string airAbsorptionLowTooltip = $"The low frequency (up to 800 Hz) EQ value for air absorption. Only used if Air Absorption Input is set to User Defined. 0 = low frequencies are completely attenuated, 1 = low frequencies are not attenuated at all." + EditorTrial.trialTooltip;

        public static readonly string airAbsorptionMidLabel = $"Mid";
        public static readonly string airAbsorptionMidTooltip = $"The middle frequency (800 Hz - 8 kHz) EQ value for air absorption. Only used if Air Absorption Input is set to User Defined. 0 = middle frequencies are completely attenuated, 1 = middle frequencies are not attenuated at all." + EditorTrial.trialTooltip;

        public static readonly string airAbsorptionHighLabel = $"High";
        public static readonly string airAbsorptionHighTooltip = $"The high frequency (8 kHz and above) EQ value for air absorption. Only used if Air Absorption Input is set to User Defined. 0 = high frequencies are completely attenuated, 1 = high frequencies are not attenuated at all." + EditorTrial.trialTooltip;

        public static readonly string directivityLabel = $"Directivity";
        public static readonly string directivityTooltip = $"If checked, attenuation based on the source’s directivity pattern and orientation will be applied to the Audio Source." + EditorTrial.trialTooltip;

        public static readonly string directivityInputLabel = $"Input";
        public static readonly string directivityInputTooltip =
       $"Specifies how the directivity attenuation value is determined." + "\n" +
       "\n" +
       $"Simulation Defined. Uses a dipole directivity model. You can control the dipole shape using the Dipole Weight and Dipole Power properties." + "\n" +
       "\n" +
       $"User Defined. Uses the value specified in the Directivity Value property. This value will not automatically change as the source rotates. You are expected to control the Directivity Value property using a custom script to achieve this effect." + EditorTrial.trialTooltip;

        public static readonly string dipoleWeightLabel = $"Dipole Weight";
        public static readonly string dipoleWeightTooltip = $"Blends between monopole (omnidirectional) and dipole directivity patterns. 0 = pure monopole (sound is emitted in all directions with equal intensity), 1 = pure dipole (sound is focused to the front and back of the source). At 0.5, the source has a cardioid directivity, with most of the sound emitted to the front of the source. Only used if Directivity Input is set to Simulation Defined." + EditorTrial.trialTooltip;

        public static readonly string dipolePowerLabel = $"Dipole Power";
        public static readonly string dipolePowerTooltip = $"Controls how focused the dipole directivity is. Higher values result in sharper directivity patterns. Only used if Directivity Input is set to Simulation Defined." + EditorTrial.trialTooltip;

        public static readonly string directivityValueLabel = $"Value";
        public static readonly string directivityValueTooltip = $"The directivity attenuation value. Only used if Directivity Input is set to User Defined. 0 = sound is completely attenuated, 1 = sound is not attenuated at all." + EditorTrial.trialTooltip;

        public static readonly string occlusionLabel = $"Occlusion";
        public static readonly string occlusionTooltip = $"If checked, attenuation based on the occlusion of the source by the scene geometry will be applied to the Audio Source." + EditorTrial.trialTooltip;

        public static readonly string occlusionInputLabel = $"Input";
        public static readonly string occlusionInputTooltip =
       $"Specifies how the occlusion attenuation value is determined." + "\n" +
       "\n" +
       $"Simulation Defined. Uses ray tracing to determine how much of the source is occluded." + "\n" +
       "\n" +
       $"User Defined. Uses the Occlusion Value slider to control occlusion. The occlusion value will not automatically change based on surrounding geometry. You are expected to control the Occlusion Value slider using a custom script to achieve this effect. This option is intended for integrating your own occlusion model with Steam Audio." + EditorTrial.trialTooltip;

        public static readonly string occlusionTypeLabel = $"Type";
        public static readonly string occlusionTypeTooltip =
       $"Specifies how rays should be traced to model occlusion." + "\n" +
       "\n" +
       $"Raycast: Trace a single ray from the listener to the source. If the ray is occluded, the source is considered occluded." + "\n" +
       "\n" +
       $"Volumetric: Trace multiple rays from the listener to the source based on the Occlusion Radius setting. The proportion of rays that are occluded determine how much of the direct sound is considered occluded. Transmission calculations, if enabled, are only applied to the occluded portion of the direct sound." + EditorTrial.trialTooltip;

        public static readonly string occlusionRadiusLabel = $"Radius";
        public static readonly string occlusionRadiusTooltip = $"The apparent size of the sound source. The larger the source radius, the larger an object must be in order to fully occlude sound emitted by the source." + EditorTrial.trialTooltip;

        public static readonly string occlusionSamplesLabel = $"Samples";
        public static readonly string occlusionSamplesTooltip = $"The number of rays to trace from the listener to various points in a sphere around the source. Only used if Occlusion Type is set to Volumetric. Increasing this number results in smoother transitions as the source becomes more (or less) occluded. This comes at the cost of increased CPU usage." + EditorTrial.trialTooltip;

        public static readonly string occlusionValueLabel = $"Value";
        public static readonly string occlusionValueTooltip = $"The occlusion attenuation value. Only used if Occlusion Input is set to User Defined. 0 = sound is completely attenuated, 1 = sound is not attenuated at all." + EditorTrial.trialTooltip;

        public static readonly string transmissionLabel = $"Transmission";
        public static readonly string transmissionTooltip = $"If checked, a filter based on the transmission of sound through occluding scene geometry will be applied to the Audio Source." + EditorTrial.trialTooltip;

        public static readonly string transmissionTypeLabel = $"Type";
        public static readonly string transmissionTypeTooltip =
       $"Specifies how the transmission filter is applied." + "\n" +
       "\n" +
       $"Frequency Independent. Transmission is modeled as a single attenuation factor." + "\n" +
       "\n" +
       $"Frequency Dependent. Transmission is modeled as a 3-band EQ." + EditorTrial.trialTooltip;

        public static readonly string transmissionInputLabel = $"Input";
        public static readonly string transmissionInputTooltip =
       $"Specifies how the transmission attenuation or EQ values are determined." + "\n" +
       "\n" +
       $"Simulation Defined. Uses ray tracing to determine how much of the sound is transmitted." + "\n" +
       "\n" +
       $"User Defined. Uses the Transmission Low, Transmission Mid, and Transmission High sliders to control transmission. The transmission values will not automatically change based on surrounding geometry. You are expected to control the sliders using a custom script to achieve this effect. This option is intended for integrating your own occlusion and transmission model with Steam Audio." + EditorTrial.trialTooltip;

        public static readonly string transmissionLowLabel = $"Low";
        public static readonly string transmissionLowTooltip = $"The low frequency (up to 800 Hz) EQ value for transmission. Only used if Transmission Input is set to User Defined. 0 = low frequencies are completely attenuated, 1 = low frequencies are not attenuated at all." + EditorTrial.trialTooltip;

        public static readonly string transmissionMidLabel = $"Mid";
        public static readonly string transmissionMidTooltip = $"The middle frequency (800 Hz to 8 kHz) EQ value for transmission. Only used if Transmission Input is set to User Defined. 0 = middle frequencies are completely attenuated, 1 = middle frequencies are not attenuated at all." + EditorTrial.trialTooltip;

        public static readonly string transmissionHighLabel = $"High";
        public static readonly string transmissionHighTooltip = $"The high frequency (8 kHz and above) EQ value for transmission. Only used if Transmission Input is set to User Defined. 0 = high frequencies are completely attenuated, 1 = high frequencies are not attenuated at all." + EditorTrial.trialTooltip;

        // Was missing from the documentation, is called mid in the editor
        public static readonly string transmissionSingleBandAttenuationLabel = $"Attenuation";
        public static readonly string transmissionSingleBandAttenuationTooltip = $"The attenuation value for transmission. Only used if Transmission Input is set to User Defined. 0 = the sound is completely attenuated, 1 = the sound is not attenuated at all." + EditorTrial.trialTooltip;

        // Is called Transmission Rays in script and Max Transmission Surfaces in documentation
        public static readonly string transmissionRaysLabel = $"Max Surfaces";
        public static readonly string transmissionRaysTooltip = $"The maximum number of transmission surfaces, starting from the closest surface to the listener, whose transmission coefficients will be considered when calculating the total amount of sound transmitted. Increasing this value will result in more accurate results when multiple surfaces lie between the source and the listener, at the cost of increased CPU usage." + EditorTrial.trialTooltip;

        public static readonly string directMixLevelLabel = $"Direct Mix";
        public static readonly string directMixLevelTooltip = $"The contribution of the direct sound path to the overall mix for this Audio Source. Lower values reduce the contribution more." + EditorTrial.trialTooltip;

        public static readonly string reflectionsLabel = $"Reflections";
        public static readonly string reflectionsTooltip = $"If checked, reflections reaching the listener from the source will be simulated and applied to the Audio Source." + EditorTrial.trialTooltip;

        public static readonly string reflectionsTypeLabel = $"Type";
        public static readonly string reflectionsTypeTooltip =
       $"Specifies how reflections should be simulated for this source." + "\n" +
       "\n" +
       $"Realtime. Rays are traced in real-time, and bounced around the scene to simulate sound reflecting from the source and reaching the listener. This allows for smooth variations, and reflections off of dynamic geometry, at the cost of significant CPU usage." + "\n" +
       "\n" +
       $"Baked Static Source. The source is assumed to be static, and the listener position is used to interpolate reflected sound from baked data. This results in relatively low CPU usage, but cannot model reflections off of dynamic geometry, and requires more memory and disk space." + "\n" +
       "\n" +
       $"Baked Static Listener. The listener is assumed to be static, and the source position is used to interpolate reflected sound from baked data. This results in relatively low CPU usage, but cannot model reflections off of dynamic geometry, and requires more memory and disk space." + EditorTrial.trialTooltip;

        // This is missing from the Steam Audio documentation
        public static readonly string useDistanceCurveForReflectionsLabel = $"Use Distance Curve";
        public static readonly string useDistanceCurveForReflectionsTooltip = $"Is shown when distance attenuation is enabled and is set to curve driven." + EditorTrial.trialTooltip;

        public static readonly string currentBakedSourceLabel = $"Baked Source";
        public static readonly string currentBakedSourceTooltip = $"If Reflections Type is set to Baked Static Source, the position and orientation of the GameObject specified in this field will be used as the position and orientation of the source." + EditorTrial.trialTooltip;

        public static readonly string applyHRTFToReflectionsLabel = $"Apply HRTF";
        public static readonly string applyHRTFToReflectionsTooltip = $"If checked, applies HRTF-based 3D audio rendering to reflections. Results in an improvement in spatialization quality when using convolution or hybrid reverb, at the cost of slightly increased CPU usage. Default: off." + EditorTrial.trialTooltip;

        public static readonly string reflectionsMixLevelLabel = $"Mix Level";
        public static readonly string reflectionsMixLevelTooltip = $"The contribution of reflections to the overall mix for this Audio Source. Lower values reduce the contribution more." + EditorTrial.trialTooltip;

        public static readonly string pathingLabel = $"Pathing";
        public static readonly string pathingTooltip = $"If checked, shortest paths taken by sound as it propagates from the source to the listener will be simulated, and appropriate spatialization will be applied to the Audio Source for these indirect paths." + EditorTrial.trialTooltip;

        public static readonly string pathingProbeBatchLabel = $"Probe Batch";
        public static readonly string pathingProbeBatchTooltip = $"When simulating pathing, the baked data stored in this probe batch will be used to look up paths from the source to the listener." + EditorTrial.trialTooltip;

        public static readonly string pathValidationLabel = $"Validation";
        public static readonly string pathValidationTooltip = $"If checked, each baked path from the source to the listener is checked in real-time to see if it is occluded by dynamic geometry. If so, the path is not rendered." + EditorTrial.trialTooltip;

        public static readonly string findAlternatePathsLabel = $"Find Alternate";
        public static readonly string findAlternatePathsTooltip = $"If checked, if a baked path from the source to the listener is found to be occluded by dynamic geometry, alternate paths are searched for in real-time, which account for the dynamic geometry." + EditorTrial.trialTooltip;

        public static readonly string applyHRTFToPathingLabel = $"Apply HRTF";
        public static readonly string applyHRTFToPathingTooltip = $"If checked, applies HRTF-based 3D audio rendering to pathing. Results in an improvement in spatialization quality, at the cost of slightly increased CPU usage. Default: off." + EditorTrial.trialTooltip;

        public static readonly string pathingMixLevelLabel = $"Mix Level";
        public static readonly string pathingMixLevelTooltip = $"The contribution of pathing to the overall mix for this Audio Source. Lower values reduce the contribution more." + EditorTrial.trialTooltip;

        // End of Steam Audio license, resuming original Sonigon copyright
    }
}
#endif