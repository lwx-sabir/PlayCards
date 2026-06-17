// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if UNITY_EDITOR

using UnityEngine;
using UnityEngine.Audio;

namespace Sonity.Internal {

    public class EditorTextSoundContainer {

        public static readonly string soundContainerTooltip =
        $"{nameof(NameOf.SoundContainer)}s are the building blocks of Sonity." + "\n" +
        "\n" +
        $"They contain {nameof(AudioClip)}s and options of how the sound should be played." + "\n" +
        "\n" +
        $"All {nameof(NameOf.SoundContainer)}s are multi-object editable." + EditorTrial.trialTooltip;

        public static readonly string presetsLabel = "Presets";
        public static readonly string presetsTooltip =
            "SFX 3D:" + "\n" +
            "Enable Distance = true" + "\n" +
            "Spatial Blend = 1f" + "\n" +
            "Never Steal Voice = false" + "\n" +
            "Never Steal Voice Effects = false" + "\n" +
            "Pitch Random = true" + "\n" +
            "Priority = 0.5" + "\n" +
            "Reverb Zone Mix = -0 dB" + "\n" +
            "\n" +
            "SFX 2D:" + "\n" +
            "Enable Distance = false" + "\n" +
            "Spatial Blend = 0" + "\n" +
            "Never Steal Voice = false" + "\n" +
            "Never Steal Voice Effects = false" + "\n" +
            "Pitch Random = true" + "\n" +
            "Priority = 0.5" + "\n" +
            "Reverb Zone Mix = -0 dB" + "\n" +
            "\n" +
            "UI:" + "\n" +
            "Enable Distance = false" + "\n" +
            "Spatial Blend = 0" + "\n" +
            "Never Steal Voice = false" + "\n" +
            "Never Steal Voice Effects = false" + "\n" +
            "Pitch Random = false" + "\n" +
            "Priority = 0.5" + "\n" +
            "Reverb Zone Mix = Negative Infinity dB" + "\n" +
            "\n" +
            "Music:" + "\n" +
            "Enable Distance = false" + "\n" +
            "Spatial Blend = 0" + "\n" +
            "Never Steal Voice = true" + "\n" +
            "Never Steal Voice Effects = true" + "\n" +
            "Pitch Random = false" + "\n" +
            "Volume Random = false" + "\n" +
            "Priority = 1" + "\n" +
            "Reverb Zone Mix = Negative Infinity dB" + "\n" +
            "\n" +
            "Automatic Looping:" + "\n" +
            $"If the name of the selected {nameof(NameOf.SoundContainer)}s contains “loop” then it will automatically enable “Loop”, “Follow Position”, “Stop if Transform is Null” and “Random Start Position”." + "\n" +
            "\n" +
            "Automatic Crossfades:" + "\n" +
            $"If the names of the selected {nameof(NameOf.SoundContainer)}s end in certain combinations it will automatically set up distance or intensity crossfades." + "\n" +
            "It works on multiple groups at the same time." + "\n" +
            "These are the combinations and their result:" + "\n" +
            "\n" +
            "Distance Crossfade:" + "\n" +
            "Close + Distant + Far = 3 layers" + "\n" +
            "Close + Distant = 2 layers" + "\n" +
            "Close + Far = 2 layers" + "\n" +
            "\n" +
            "Intensity Crossfade:" + "\n" +
            "Soft + Medium + Hard = 3 layers" + "\n" +
            "Soft + Hard = 2 layers" + "\n" +
            "\n" +
            $"{nameof(NameOf.SoundPreset)}:" + "\n" +
            $"Create {nameof(NameOf.SoundPreset)} objects to get custom settings which you can apply to your {nameof(NameOf.SoundContainer)}s and {nameof(NameOf.SoundEvent)}s." + "\n" +
            $"Either manually select the preset you want to apply in the dropdown, or use “Auto Match” to automatically apply the settings based on the name of the asset." + EditorTrial.trialTooltip;

        // Spatialization Info /////////////////////////////////////////////////////////////////////
        public static readonly string spatializationInfo3DSoundSingleLabel = "3D Sound";
        public static readonly string spatializationInfo3DSoundMultipleLabel = "3D Sounds";
        public static readonly string spatializationInfo3DSoundTooltip =
            $"All {nameof(NameOf.SoundContainer)}s with Spatial Blend > 0 and Distance Enabled." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfo2DSoundSingleLabel = "2D Sound";
        public static readonly string spatializationInfo2DSoundMultipleLabel = "2D Sounds";
        public static readonly string spatializationInfo2DSoundTooltip =
            $"All {nameof(NameOf.SoundContainer)}s with Spatial Blend == 0 and Distance Disabled." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfo3DSoundWithoutDistanceSingleLabel = "3D Sound no Distance";
        public static readonly string spatializationInfo3DSoundWithoutDistanceMultipleLabel = "3D Sounds no Distance";
        public static readonly string spatializationInfo3DSoundWithoutDistanceTooltip =
            $"All {nameof(NameOf.SoundContainer)}s with Spatial Blend > 0 and Distance Disabled." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfo2DSoundWithDistanceSingleLabel = "2D Sound with Distance";
        public static readonly string spatializationInfo2DSoundWithDistanceMultipleLabel = "2D Sounds with Distance";
        public static readonly string spatializationInfo2DSoundWithDistanceTooltip =
            $"All {nameof(NameOf.SoundContainer)}s with Spatial Blend == 0 and Distance Enabled." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfoSelectSCLabel = "Select SC";
        public static readonly string spatializationInfoSelectSCTooltip =
            $"Selects {nameof(NameOf.SoundContainer)}s matching the specific settings." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfoSelectAllSCLabel = "Select All SC";
        public static readonly string spatializationInfoSelectAllSCTooltip =
            $"Selects All {nameof(NameOf.SoundContainer)}s in the same folder disregarding subfolders." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfoSelectACLabel = "Select AC";
        public static readonly string spatializationInfoSelectACTooltip =
            $"Selects AudioClips of the {nameof(NameOf.SoundContainer)}s matching the specific settings." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfoSet3DLabel = "Set 3D";
        public static readonly string spatializationInfoSet3DTooltip =
            $"Sets the selected {nameof(NameOf.SoundContainer)}s to Spatial Blend 1 and Distance Enabled." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfoSet2DLabel = "Set 2D";
        public static readonly string spatializationInfoSet2DTooltip =
            $"Sets the selected {nameof(NameOf.SoundContainer)}s to Spatial Blend 0 and Distance Disabled." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfoEditAllLabel = "Edit All";
        public static readonly string spatializationInfoEditAllTooltip =
            $"Edits all the selected {nameof(NameOf.SoundContainer)}s." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfoAllSelectACLabel = "All Select AC";
        public static readonly string spatializationInfoAllSelectACTooltip =
            $"Selects all AudioClips of the selected {nameof(NameOf.SoundContainer)}s." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfoAllSet3DLabel = "All Set 3D";
        public static readonly string spatializationInfoAllSet3DTooltip =
            $"Sets all selected {nameof(NameOf.SoundContainer)}s to Spatial Blend 1 and Distance Enabled." + EditorTrial.trialTooltip;

        public static readonly string spatializationInfoAllSet2DLabel = "All Set 2D";
        public static readonly string spatializationInfoAllSet2DTooltip =
            $"Sets all selected {nameof(NameOf.SoundContainer)}s to Spatial Blend 0 and Distance Disabled." + EditorTrial.trialTooltip;
        // Spatialization Info /////////////////////////////////////////////////////////////////////

        public static readonly string updateAudioClipsLabel = "Update AudioClips";
        public static readonly string updateAudioClipsTooltip =
            "Updating AudioClips can be used to automatically add/remove variations." + "\n" +
            "Can be used on multiple items at once (tip: search \"t:soundContainer\")." + "\n" +
            "\n" +
            $"Refresh {nameof(AudioClip)} Group" + "\n" +
            $"Adds all {nameof(AudioClip)}s with the same name as the first {nameof(AudioClip)} (disregarding numbers, e.g. 01 02)." + "\n" +
            "\n" +
            $"Find AudioClip Group" + "\n" +
            $"Automatically finds all {nameof(AudioClip)}s containing the same name as this {nameof(NameOf.SoundContainer)} (disregarding _SC, numbers)." + "\n" +
            "\n" +
            $"If no matching {nameof(AudioClip)}s are found, it will try and remove one character at the end of the name at a time until it finds a hit." + EditorTrial.trialTooltip;

        public static readonly string findReferencesLabel = $"Find References";
        public static readonly string findReferencesTooltip = $"Finds all the references to the {nameof(NameOf.SoundEvent)}." + EditorTrial.trialTooltip;

        public static readonly string findReferencesSelectAllLabel = $"Select All";
        public static readonly string findReferencesSelectAllTooltip = $"Selects all the assets with references to the {nameof(NameOf.SoundContainer)}." + EditorTrial.trialTooltip;

        public static readonly string findReferencesClearLabel = $"Clear";
        public static readonly string findReferencesClearTooltip = $"Removes all the found references." + EditorTrial.trialTooltip;

        // Warning
        public static readonly string audioClipWarningEmpty = "Empty AudioClip";
        public static readonly string audioClipWarningNull = "Null AudioClip";

        public static readonly string enableDistanceLabel = "Enable Distance";
        public static readonly string enableDistanceTooltip = $"Otherwise the {nameof(NameOf.SoundContainer)} will not be affected by distance (disable for music etc)." + EditorTrial.trialTooltip;

        public static readonly string distanceScaleLabel = "Distance Scale";
        public static readonly string distanceScaleTooltip = 
            $"Range scale multiplier." +
            "\n" +
            $"It is multiplied by the Distance Scale of the {nameof(NameOf.SoundManager)}." + EditorTrial.trialTooltip;

        public static readonly string distanceScaleIfXSetToYValueLabel = "If X set to Y";
        public static readonly string distanceScaleIfXSetToYValueTooltip =
            $"Shows up if you have select multiple {nameof(NameOf.SoundContainer)}s with Distance Enabled." +
            "\n" +
            $"Useful for batch editing a lot of assets where you want to change the Distance Scale for many sounds." + EditorTrial.trialTooltip;

        public static readonly string distanceScaleIfXSetToYButtonLabel = "Replace Distance";
        public static readonly string distanceScaleIfXSetToYButtonTooltip =
            $"Replaces any Distance Scale matching X with the Y value." +
            "\n" +
            $"Also shows the Min and Max value for Distance Scale of the selected objects." + EditorTrial.trialTooltip;

        public static readonly string loopLabel = "Loop";
        public static readonly string loopTooltip =
            $"Makes the sound loop." + "\n" +
            "\n" +
            "If you use \"Create Assets from Selection\"  and the AudioClip name contains \"loop\"" + "\n" +
            "\n" +
            "Then it will automatically enable “Loop”, “Follow Position”, “Stop if Transform is Null” and “Random Start Position”." + EditorTrial.trialTooltip;

        public static readonly string followPositionLabel = "Follow Position";
        public static readonly string followPositionTooltip = $"If the {nameof(NameOf.SoundContainer)} should follow the given Transform position." + EditorTrial.trialTooltip;

        public static readonly string stopIfTransformIsNullLabel = "Stop if Transform is Null";
        public static readonly string stopIfTransformIsNullTooltip =
            "Automatically stops the sound if the Transform it's played at is destroyed (either the owner or position Transform)." + "\n" +
            "\n" +
            "Useful safety precaution for loops." + EditorTrial.trialTooltip;

        public static readonly string virtualizeLabel = "Virtualize";
        public static readonly string virtualizeTooltip = "" + EditorTrial.trialTooltip;

        public static readonly string randomStartPositionLabel = "Random Start Position";
        public static readonly string randomStartPositionTooltip = 
            "Starts the sound at a random position." + "\n" +
            "\n" +
            "Overrides the Start Position setting." + "\n" +
            "\n" +
            "Useful for loops." + EditorTrial.trialTooltip;

        public static readonly string randomStartPositionMinMaxLabel = "Random Range";
        public static readonly string randomStartPositionMinMaxTooltip = "Min/max range within the sound can start at." + EditorTrial.trialTooltip;

        public static readonly string startPositionLabel = "Start Position";
        public static readonly string startPositionTooltip = "0 is the start and 1 is the end." + EditorTrial.trialTooltip;

        public static readonly string reverseLabel = "Reverse";
        public static readonly string reverseTooltip =
            $"If enabled the AudioClip will be played backwards." + "\n" +
            "\n" +
            $"Make sure to set the start position to the end." + "\n" +
            "\n" +
            $"Reverse is only supported for AudioClips which are stored in an uncompressed format or will be decompressed at load time." + EditorTrial.trialTooltip;

        // SoundContainer Settings Warnings
        public static readonly string distanceScaleWarningLabel = $"Distance Scale is 0. The {nameof(NameOf.SoundContainer)} will not be heard";

        public static readonly string lockAxisEnableLabel = $"Lock Axis";
        public static readonly string lockAxisEnableTooltip = 
            "Locks the selected axis to the selected position." + "\n" +
            "\n" +
            "Useful for 2D games if you want to lock the sound to a position along an axis." + EditorTrial.trialTooltip;

        public static readonly string lockAxisLabel = $"Axis";
        public static readonly string lockAxisTooltip = "The axis to lock." + EditorTrial.trialTooltip;

        public static readonly string lockAxisPositionLabel = $"Position";
        public static readonly string lockAxisPositionTooltip = "The position to set the locked axis to." + EditorTrial.trialTooltip;

        public static readonly string preventEndClicksLabel = "Prevent End Clicks";
        public static readonly string preventEndClicksTooltip =
            $"If enabled it will fade out the volume 0.1 seconds before the end of the AudioClip to prevent clicks." + "\n" +
            "\n" +
            $"If the AudioClips is shorter than 0.1 seconds or set to loop the fade will be skipped." + "\n" +
            "\n" +
            $"DC offsets and some settings in Unity make an AudioClip click at the end." + "\n" +
            "\n" +
            $"Tip: If you still experience sporadic clicks, try changing the Load Type of the AudioClips to e.g. \"Compressed In Memory\", it might help." + EditorTrial.trialTooltip;

        public static readonly string priorityLabel = "Priority";
        public static readonly string priorityTooltip =
            "The priority the Voice has when Voice stealing." + "\n" +
            "\n" +
            "Also the priority the Voice Effects has when Voice Effects stealing." + "\n" +
            "\n" +
            "1 is high priority, 0.5 is default priority and 0 is low priority." + "\n" +
            "\n" +
            "It's multiplied with the volume of the Voice when evaluating final priority." + EditorTrial.trialTooltip;

        public static readonly string neverStealVoiceLabel = "Never Steal Voice";
        public static readonly string neverStealVoiceTooltip =
            $"The {nameof(NameOf.SoundManager)} will never steal this Voice if the Voice Limit is reached (use on music etc)." + EditorTrial.trialTooltip;

        public static readonly string neverStealVoiceEffectsLabel = "Never Steal Voice Effects";
        public static readonly string neverStealVoiceEffectsTooltip =
            $"The {nameof(NameOf.SoundManager)} will never steal the Voice Effects on this Voice if the Voice Effect Limit is reached (use on music etc)." + EditorTrial.trialTooltip;

        public static readonly string playOrderLabel = $"Play Order";
        public static readonly string playOrderTooltip =
            $"Determines in which order the AudioClips will be played " + "\n" +
            "\n" +
            $"Global Random" + "\n" +
            $"All {nameof(NameOf.SoundEvent)}s will share the same global random {nameof(AudioClip)} pool, which ensures less repetition." + "\n" +
            $"Uses a pseudo random function remembering half of the length of available {nameof(AudioClip)}s it last played to avoid repetition." + "\n" +
            "\n" +
            $"Local Random" + "\n" +
            $"Same as global random except its per {nameof(NameOf.SoundEvent)} owner." + EditorTrial.trialTooltip;


        public static readonly string dopplerAmountLabel = "Doppler Amount";
        public static readonly string dopplerAmountTooltip =
            $"How much the pitch of the sound is changed by the relative velocity between the {nameof(AudioListener)} and the {nameof(AudioSource)}." + EditorTrial.trialTooltip;

        public static readonly string bypassReverbZonesLabel = "Bypass Reverb Zones";
        public static readonly string bypassReverbZonesTooltip = "Bypasses any reverb zones" + EditorTrial.trialTooltip;

        public static readonly string bypassVoiceEffectsLabel = "Bypass Voice Effects";
        public static readonly string bypassVoiceEffectsTooltip = 
            "Bypasses any effects on the AudioSource, e.g. Distortion and Filters." + "\n" +
            "\n" +
            "Voice effects are automatically bypassed if you don't have distortion/lowpass/highpass enabled." + EditorTrial.trialTooltip;

        public static readonly string bypassListenerEffectsLabel = "Bypass Listener Effects";
        public static readonly string bypassListenerEffectsTooltip = "Bypasses any effects on the listener" + EditorTrial.trialTooltip;

        // HRTF Plugin Spatialize
        public static readonly string hrtfPluginSpatializeLabel = "HRTF Plugin Spatialize";
        public static readonly string hrtfPluginSpatializeTooltip =
            $"Enables spatialization using external third party plugins." + "\n" +
            "\n" +
            $"You need to add the plugin and select it in the project audio settings." + "\n" +
            "\n" +
            $"Example of third party spatializers:" + "\n" +
            "\n" +
            $"Steam Audio Spatializer" + "\n" +
            $"Oculus Spatializer Unity" + "\n" +
            $"Google Resonance Audio Spatializer" + "\n" +
            $"Vive 3DSP Audio SDK Spatializer" + "\n" +
            $"Microsoft Spatializer" + "\n" +
            $"Apple PHASE Spatializer" + "\n" +
            $"Qualcomm 3D Spatializer" + EditorTrial.trialTooltip;

        public static readonly string hrtfPluginSpatializePostEffectsLabel = "HRTF Plugin Post Effects";
        public static readonly string hrtfPluginSpatializePostEffectsTooltip = 
            "If the spatialization plugin effect happens before before or after any effects." + EditorTrial.trialTooltip;

        // Volume
        public static readonly string volumeLabel = "Volume dB";
        public static readonly string volumeTooltip =
            $"Volume offset in decibel." + "\n" +
            "\n" +
            $"If you want to be able to raise the volume and not just lower it you have 2 options:" + "\n" +
            "\n" +
            $"Option A:" + "\n" +
            "\n" +
            $"Enable \"Enable Volume Increase\" in the {nameof(NameOf.SoundManager)}." + "\n" +
            "\n" +
            $"This will enable you to raise the volumes of {nameof(NameOf.SoundContainer)}s and {nameof(NameOf.SoundEvent)}s by +24 dB each and {nameof(NameOf.SoundVolumeGroup)}s by +12dB." + "\n" +
            "\n" +
            $"Option B:" + "\n" +
            "\n" +
            $"Select all the {nameof(NameOf.SoundContainer)}s and lower the volume by -20 dB with the -1dB button." + "\n" +
            "\n" +
            $"Then to compensate you can increase the global volume with an Audio Mixer (which you then can set to +20 dB)." + EditorTrial.trialTooltip;

        public static readonly string volumeRelativeLowerLabel = "-1 dB";
        public static readonly string volumeRelativeLowerTooltip = 
            $"Lowers the relative volume of all the selected {nameof(NameOf.SoundContainer)}s." + "\n" +
            "\n" +
            $"Useful for example if you want to raise the volume of one {nameof(NameOf.SoundContainer)} and keep the relative volume." + "\n" +
            "\n" +
            $"Because then you can lower all of them to get more headroom." + "\n" +
            "\n" +
            $"If multiple {nameof(NameOf.SoundContainer)}s are selected it will show the lowest volume." + EditorTrial.trialTooltip;

        public static readonly string volumeRelativeIncreaseLabel = "+1 dB";
        public static readonly string volumeRelativeIncreaseTooltip =
            $"Raises the relative volume of all the selected {nameof(NameOf.SoundContainer)}s." + "\n" +
            "\n" +
            $"Useful for example if you want to set the loudest volume to 0 dB but keep the relative volumes." + "\n" +
            "\n" +
            $"If multiple {nameof(NameOf.SoundContainer)}s are selected it will show the highest volume." + EditorTrial.trialTooltip;

        public static readonly string volumeOverLimitWarning =
            $"Volume is over 0dB, please add scripting define symbol:" + "\n" +
            $"\"SONITY_ENABLE_VOLUME_INCREASE\" or lower the volume." + "\n" +
            $"Tip: Select all {nameof(NameOf.SoundContainer)}s and press \"- 1dB\" until max is 0dB.";

        public static readonly string volumeRandomLabel = "Random";
        public static readonly string volumeRandomTooltip = "Toggles random volume." + EditorTrial.trialTooltip;

        public static readonly string volumeRandomRangeLabel = "Range dB";
        public static readonly string volumeRandomRangeTooltip = "Amount of random volume to lower by in decibel." + EditorTrial.trialTooltip;

        public static readonly string volumeDistanceLabel = "Distance";
        public static readonly string volumeDistanceTooltip = $"Changes the sound over distance." + EditorTrial.trialTooltip;

        public static readonly string volumeDistanceRolloffLabel = "Rolloff";
        public static readonly string volumeDistanceRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string volumeDistanceCurveLabel = "Curve";
        public static readonly string volumeDistanceCurveTooltip = "Curve of the volume over distance.\n\nFrom 0 (close) to 1 (distant)." + EditorTrial.trialTooltip;

        public static readonly string volumeDistanceCrossfadeEnabledLabel = "Distance Crossfade";
        public static readonly string volumeDistanceCrossfadeEnabledTooltip =
            $"With distance crossfade you can easily crossfade between different sounds over distance." +
            "\n" +
            $"For e.g. gunshots you could add sounds with close, distant and far perspectives." +
            "\n" +
            $"You’d set the “Layers” setting to 3 for all the {nameof(NameOf.SoundContainer)}s." +
            "\n" +
            $"Then you’d set “This Is” of close to 1, distant to 2, and far to 3." + EditorTrial.trialTooltip;

        public static readonly string volumeDistanceCrossfadeLayersLabel = "Layers";
        public static readonly string volumeDistanceCrossfadeLayersTooltip = 
            "The number of layers the crossfade is based on." +
            "\n" +
            "You must have at least 2 layers." + EditorTrial.trialTooltip;

        public static readonly string volumeDistanceCrossfadeThisIsLabel = "This Is";
        public static readonly string volumeDistanceCrossfadeThisIsTooltip = 
            $"Which layer this is." + "\n" +
            "\n" +
            $"Set up with other {nameof(NameOf.SoundContainer)}s for the other layers." + "\n" +
            "\n" +
            $"Lower numbers are closer and higher are more distant." + EditorTrial.trialTooltip;

        public static readonly string volumeDistanceCrossfadeRolloffLabel = "Rolloff";
        public static readonly string volumeDistanceCrossfadeRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string volumeDistanceCrossfadeCurveLabel = "Curve";
        public static readonly string volumeDistanceCrossfadeCurveTooltip = 
            "How the layers are crossfaded over distance." + "\n" +
            "\n" +
            "Standard is from 0 (close) to 1 (distant)." + EditorTrial.trialTooltip;

        public static readonly string volumeIntensityEnableLabel = "Intensity";
        public static readonly string volumeIntensityEnableTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string volumeIntensityRolloffLabel = "Rolloff";
        public static readonly string volumeIntensityRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string volumeIntensityStrengthLabel = "Strength";
        public static readonly string volumeIntensityStrengthTooltip = "How much effect the intensity should have." + EditorTrial.trialTooltip;

        public static readonly string volumeIntensityCurveLabel = "Curve";
        public static readonly string volumeIntensityCurveTooltip = "Curve of the volume over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        public static readonly string volumeIntensityCrossfadeEnabledLabel = "Intensity Crossfade";
        public static readonly string volumeIntensityCrossfadeEnabledTooltip =
            $"With intensity crossfade you can easily crossfade between different sounds over intensity." +
            "\n" +
            $"For e.g. impacts you could add sounds with hard, medium and soft variations." +
            "\n" +
            $"You’d set the “Layers” setting to 3 for all the {nameof(NameOf.SoundContainer)}s." +
            "\n" +
            $"Then you’d set “This Is” of hard to 3, medium to 2 and soft to 1." + EditorTrial.trialTooltip;

        public static readonly string volumeIntensityCrossfadeLayersLabel = "Layers";
        public static readonly string volumeIntensityCrossfadeLayersTooltip = 
            "The number of layers the crossfade is based on." + "\n" +
            "\n" +
            "You must have at least 2 layers." + EditorTrial.trialTooltip;

        public static readonly string volumeIntensityCrossfadeThisIsLabel = "This Is";
        public static readonly string volumeIntensityCrossfadeThisIsTooltip = 
            $"Which layer this is." + "\n" +
            "\n" +
            $"Set up with other {nameof(NameOf.SoundContainer)}s for the other layers." + "\n" +
            "\n" +
            $"Higher numbers are harder and lower numbers are softer." + EditorTrial.trialTooltip;

        public static readonly string volumeIntensityCrossfadeRolloffLabel = "Rolloff";
        public static readonly string volumeIntensityCrossfadeRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string intensityCrossfadeCurveLabel = "Curve";
        public static readonly string intensityCrossfadeCurveTooltip = "How the layers are crossfaded over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        // Pitch
        public static readonly string pitchLabel = "Pitch st";
        public static readonly string pitchTooltip = $"The pitch in semitones.\n\nRange -24 to +24." + EditorTrial.trialTooltip;

        public static readonly string pitchRandomLabel = "Random";
        public static readonly string pitchRandomTooltip = "Toggle random pitch." + EditorTrial.trialTooltip;

        public static readonly string pitchRandomRangeLabel = "Range st";
        public static readonly string pitchRandomRangeTooltip = "Amount of random pitch variation in semitones (bipolar)." + EditorTrial.trialTooltip;

        public static readonly string pitchIntensityEnableLabel = "Intensity";
        public static readonly string pitchIntensityEnableTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string pitchIntensityBaseLabel = "Low st";
        public static readonly string pitchIntensityBaseTooltip = "The lowest intensity in semitones.\n\nRange -128 to 128" + EditorTrial.trialTooltip;

        public static readonly string pitchIntensityRangeLabel = "High st";
        public static readonly string pitchIntensityRangeTooltip = "The highest intensity in semitones.\n\nRange -128 to 128" + EditorTrial.trialTooltip;

        public static readonly string pitchIntensityRolloffLabel = "Rolloff";
        public static readonly string pitchIntensityRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string pitchIntensityCurveLabel = "Curve";
        public static readonly string pitchIntensityCurveTooltip = "Curve of the pitch over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        // Spatial Blend
        public static readonly string spatialBlendBaseLabel = "Spatial Blend";
        public static readonly string spatialBlendBaseTooltip = "Amount of spatial blend.\n\n0 is 2D and 1 is 3D." + EditorTrial.trialTooltip;

        public static readonly string spatialBlendDistanceLabel = "Distance";
        public static readonly string spatialBlendDistanceTooltip = $"Changes the sound over distance." + EditorTrial.trialTooltip;

        public static readonly string spatialBlendDistanceRolloffLabel = "Rolloff";
        public static readonly string spatialBlendDistanceRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string spatialBlendDistance3DIncreaseLabel = "Increase";
        public static readonly string spatialBlendDistance3DIncreaseTooltip = "Increase the amount of spatial blend." + EditorTrial.trialTooltip;

        public static readonly string spatialBlendDistanceCurveLabel = "Curve";
        public static readonly string spatialBlendDistanceCurveTooltip = "Curve of the spatial blend over distance.\n\nFrom 0 (close) to 1 (distant)." + EditorTrial.trialTooltip;

        public static readonly string spatialBlendIntensityEnableLabel = "Intensity";
        public static readonly string spatialBlendIntensityEnableTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string spatialBlendIntensityRolloffLabel = "Rolloff";
        public static readonly string spatialBlendIntensityRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string spatialBlendIntensityStrengthLabel = "Strength";
        public static readonly string spatialBlendIntensityStrengthTooltip = "How much effect the intensity should have." + EditorTrial.trialTooltip;

        public static readonly string spatialBlendIntensityCurveLabel = "Curve";
        public static readonly string spatialBlendIntensityCurveTooltip = "Curve of the spatial blend over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        // Spatial Spread
        public static readonly string spatialSpreadBaseLabel = "Spatial Spread °";
        public static readonly string spatialSpreadBaseTooltip = 
            "From 0 to 360 degrees." + "\n" +
            "\n" +
            "Only the 3D part of the sound is affected by the spatial spread." + EditorTrial.trialTooltip;

        public static readonly string spatialSpreadDistanceLabel = "Distance";
        public static readonly string spatialSpreadDistanceTooltip = $"Changes the sound over distance." + EditorTrial.trialTooltip;

        public static readonly string spatialSpreadDistanceRolloffLabel = "Rolloff";
        public static readonly string spatialSpreadDistanceRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string spatialSpreadDistanceCurveLabel = "Curve";
        public static readonly string spatialSpreadDistanceCurveTooltip = "Curve of the spatial spread over distance.\n\nFrom 0 (close) to 1 (distant)." + EditorTrial.trialTooltip;

        public static readonly string spatialSpreadIntensityEnableLabel = "Intensity";
        public static readonly string spatialSpreadIntensityEnableTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string spatialSpreadIntensityRolloffLabel = "Rolloff";
        public static readonly string spatialSpreadIntensityRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string spatialSpreadIntensityStrengthLabel = "Strength";
        public static readonly string spatialSpreadIntensityStrengthTooltip = "How much effect the intensity should have." + EditorTrial.trialTooltip;

        public static readonly string spatialSpreadIntensityCurveLabel = "Curve";
        public static readonly string spatialSpreadIntensityCurveTooltip = "Curve of the spatial spread over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        // Stereo Pan
        public static readonly string stereoPanOffsetLabel = "Stereo Pan L/R";
        public static readonly string stereoPanOffsetTooltip =
            "-1 is left and 1 right." + "\n" +
            "\n" +
            "Only the 2D part of the sound is affected by the stereo pan." + EditorTrial.trialTooltip;

        public static readonly string stereoPanAngleToSteroPanUseLabel = "Angle To Stereo Pan";
        public static readonly string stereoPanAngleToSteroPanUseTooltip =
            "Pans the sound depending on the angle between the Voice and the AudioListener." + "\n" +
            "\n" +
            "Only the 2D part of the sound is affected by the stereo pan." + EditorTrial.trialTooltip;

        public static readonly string stereoPanAngleToSteroPanStrengthLabel = "Strength";
        public static readonly string stereoPanAngleToSteroPanStrengthTooltip = "The amount of angle to stereo pan." + EditorTrial.trialTooltip;

        public static readonly string stereoPanAngleToSteroPanRolloffLabel = "Rolloff";
        public static readonly string stereoPanAngleToSteroPanRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        // Reverb Zone Mix
        public static readonly string reverbZoneMixDecibelLabel = "Reverb dB";
        public static readonly string reverbZoneMixDecibelTooltip = "The amount of reverb zone send in decibel." + EditorTrial.trialTooltip;

        public static readonly string reverbZoneMixDistanceLabel = "Distance";
        public static readonly string reverbZoneMixDistanceTooltip = $"Changes the sound over distance." + EditorTrial.trialTooltip;

        public static readonly string reverbZoneMixDistanceRolloffLabel = "Rolloff";
        public static readonly string reverbZoneMixDistanceRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string reverbZoneMixDistanceIncreaseLabel = "Increase";
        public static readonly string reverbZoneMixDistanceIncreaseTooltip = "Increase the amount of reverb mix." + EditorTrial.trialTooltip;

        public static readonly string reverbZoneMixDistanceCurveLabel = "Curve";
        public static readonly string reverbZoneMixDistanceCurveTooltip = "Curve of the reverb zone over distance.\n\nFrom 0 (close) to 1 (distant)." + EditorTrial.trialTooltip;

        public static readonly string reverbZoneMixIntensityEnableLabel = "Intensity";
        public static readonly string reverbZoneMixIntensityEnableTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string reverbZoneMixIntensityRolloffLabel = "Rolloff";
        public static readonly string reverbZoneMixIntensityRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string reverbZoneMixIntensityStrengthLabel = "Strength";
        public static readonly string reverbZoneMixIntensityStrengthTooltip = "How much effect the intensity should have." + EditorTrial.trialTooltip;

        public static readonly string reverbZoneMixIntensityCurveLabel = "Curve";
        public static readonly string reverbZoneMixIntensityCurveTooltip = "Curve of the reverb zone over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        // Distortion
        public static readonly string distortionEnableLabel = "Distortion";
        public static readonly string distortionEnableTooltip = 
            "Waveshaper type distortion." + "\n" +
            "\n" +
            "0 is unchanged, 1 is distorted." + "\n" +
            "\n" +
            $"{nameof(NameOf.SoundContainer)} Voice Effects are applied per Voice." + "\n" +
            "\n" +
            "If distortion amount is 0 the effect is disabled internally for performance." + "\n" +
            "\n" +
            $"The number of active Voice Effects are limited by the “Voice Effect Limit” on the {nameof(NameOf.SoundManager)}." + "\n" +
            "\n" +
            "DSP effects are not available in WebGL." + EditorTrial.trialTooltip;

        public static readonly string distortionAmountLabel = "Amount";
        public static readonly string distortionAmountTooltip =
            "The amount of distortion." + "\n" +
            "\n" +
            "0 is clean, 1 is distorted." + EditorTrial.trialTooltip;

        public static readonly string distortionDistanceLabel = "Distance";
        public static readonly string distortionDistanceTooltip = $"Changes the sound over distance." + EditorTrial.trialTooltip;

        public static readonly string distortionDistanceRolloffLabel = "Rolloff";
        public static readonly string distortionDistanceRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string distortionDistanceCurveLabel = "Curve";
        public static readonly string distortionDistanceCurveTooltip =  
            "Curve of the distortion over distance." + "\n" +
            "\n" +
            "1 (close) is distorted, 0 (far) is clean." + EditorTrial.trialTooltip;

        public static readonly string distortionIntensityEnableLabel = "Intensity";
        public static readonly string distortionIntensityEnableTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string distortionIntensityRolloffLabel = "Rolloff";
        public static readonly string distortionIntensityRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string distortionIntensityStrengthLabel = "Strength";
        public static readonly string distortionIntensityStrengthTooltip = "How much effect the intensity should have." + EditorTrial.trialTooltip;

        public static readonly string distortionIntensityCurveLabel = "Curve";
        public static readonly string distortionIntensityCurveTooltip = 
            "Curve of the distortion over intensity." + "\n" +
            "\n" +
            "0 (soft) is clean, 1 (hard) is distortion." + EditorTrial.trialTooltip;

        // Lowpass
        public static readonly string lowpassEnableLabel = "Lowpass";
        public static readonly string lowpassEnableTooltip =
            "Lowpass filter with a variable amount." + "\n" + 
            "\n" +
            "Maximum of 6dB per octave." + "\n" +
             "\n" +
            $"{nameof(NameOf.SoundContainer)} voice effects are applied per Voice." + "\n" +
             "\n" +
            "If frequency is 20,000 Hz or amount is 0 dB the effect is disabled internally for performance." + "\n" +
             "\n" +
            $"The number of active Voice Effects are limited by the “Voice Effect Limit” on the {nameof(NameOf.SoundManager)}." + "\n" +
            "\n" +
            "DSP effects are not available in WebGL." + EditorTrial.trialTooltip;

        public static readonly string lowpassFrequencyLabel = "Frequency Hz";
        public static readonly string lowpassFrequencyTooltip = "The cutoff frequency of the lowpass filter in Hz." + EditorTrial.trialTooltip;

        public static readonly string lowpassAmountLabel = "Amount dB";
        public static readonly string lowpassAmountTooltip = "The base lowpass slope in dB per octave." + EditorTrial.trialTooltip;

        public static readonly string lowpassDistanceLabel = "Distance";
        public static readonly string lowpassDistanceTooltip = $"Changes the sound over distance." + EditorTrial.trialTooltip;

        public static readonly string lowpassDistanceFrequencyRolloffLabel = "Frequency Rolloff";
        public static readonly string lowpassDistanceFrequencyRolloffTooltip =
            "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string lowpassDistanceFrequencyCurveLabel = "Frequency Curve";
        public static readonly string lowpassDistanceFrequencyCurveTooltip =
            "Curve of the lowpass frequency over distance.\n\nFrom 0 (close) to 1 (distant)." + EditorTrial.trialTooltip;

        public static readonly string lowpassDistanceAmountRolloffLabel = "Amount Rolloff";
        public static readonly string lowpassDistanceAmountRolloffTooltip =
            "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string lowpassDistanceAmountCurveLabel = "Amount Curve";
        public static readonly string lowpassDistanceAmountCurveTooltip =
            "Curve of the lowpass slope amount over distance.\n\nFrom 0 (close) to 1 (distant)." + EditorTrial.trialTooltip;

        public static readonly string lowpassIntensityEnableLabel = "Intensity";
        public static readonly string lowpassIntensityEnableTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string lowpassIntensityFrequencyLabel = "Intensity Frequency";
        public static readonly string lowpassIntensityFrequencyTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string lowpassIntensityFrequencyRolloffLabel = "Frequency Rolloff";
        public static readonly string lowpassIntensityFrequencyRolloffTooltip =
            "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string lowpassIntensityFrequencyStrengthLabel = "Frequency Strength";
        public static readonly string lowpassIntensityFrequencyStrengthTooltip =
            "How much effect the intensity should have." + EditorTrial.trialTooltip;

        public static readonly string lowpassIntensityFrequencyCurveLabel = "Frequency Curve";
        public static readonly string lowpassIntensityFrequencyCurveTooltip =
            "Curve of the lowpass frequency over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        public static readonly string lowpassIntensityAmountLabel = "Intensity Amount";
        public static readonly string lowpassIntensityAmountTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string lowpassIntensityAmountRolloffLabel = "Amount Rolloff";
        public static readonly string lowpassIntensityAmountRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string lowpassIntensityAmountStrengthLabel = "Amount Strength";
        public static readonly string lowpassIntensityAmountStrengthTooltip = "How much effect the intensity should have." + EditorTrial.trialTooltip;

        public static readonly string lowpassIntensityAmountCurveLabel = "Amount Curve";
        public static readonly string lowpassIntensityAmountCurveTooltip = "Curve of the lowpass amount over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        // Highpass
        public static readonly string highpassEnableLabel = "Highpass";
        public static readonly string highpassEnableTooltip =
            "Highpass filter with a variable amount." + "\n" +
            "\n" +
            "Maximum of 6dB per octave." + "\n" +
            "\n" +
            $"{nameof(NameOf.SoundContainer)} voice effects are applied per Voice." + "\n" +
             "\n" +
            "If frequency is 20 Hz or amount is 0 dB the effect is disabled internally for performance." + "\n" +
             "\n" +
            $"The number of active Voice Effects are limited by the “Voice Effect Limit” on the {nameof(NameOf.SoundManager)}." + "\n" +
            "\n" +
            "DSP effects are not available in WebGL." + EditorTrial.trialTooltip;

        public static readonly string highpassFrequencyLabel = "Frequency Hz";
        public static readonly string highpassFrequencyTooltip = "The cutoff frequency of the highpass filter in Hz." + EditorTrial.trialTooltip;

        public static readonly string highpassAmountLabel = "Amount dB";
        public static readonly string highpassAmountTooltip = "The base highpass slope in dB per octave." + EditorTrial.trialTooltip;

        public static readonly string highpassDistanceLabel = "Distance";
        public static readonly string highpassDistanceTooltip = $"Changes the sound over distance." + EditorTrial.trialTooltip;

        public static readonly string highpassDistanceFrequencyRolloffLabel = "Frequency Rolloff";
        public static readonly string highpassDistanceFrequencyRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string highpassDistanceFrequencyCurveLabel = "Frequency Curve";
        public static readonly string highpassDistanceFrequencyCurveTooltip = "Curve of the highpass frequency over distance.\n\nFrom 0 (close) to 1 (distant)." + EditorTrial.trialTooltip;

        public static readonly string highpassDistanceAmountRolloffLabel = "Amount Rolloff";
        public static readonly string highpassDistanceAmountRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string highpassDistanceAmountCurveLabel = "Amount Curve";
        public static readonly string highpassDistanceAmountCurveTooltip = "Curve of the highpass slope amount over distance.\n\nFrom 0 (close) to 1 (distant)." + EditorTrial.trialTooltip;

        public static readonly string highpassIntensityEnableLabel = "Intensity";
        public static readonly string highpassIntensityEnableTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string highpassIntensityFrequencyLabel = "Intensity Frequency";
        public static readonly string highpassIntensityFrequencyTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string highpassIntensityFrequencyRolloffLabel = "Frequency Rolloff";
        public static readonly string highpassIntensityFrequencyRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string highpassIntensityFrequencyStrengthLabel = "Frequency Strength";
        public static readonly string highpassIntensityFrequencyStrengthTooltip = "How much effect the intensity should have." + EditorTrial.trialTooltip;

        public static readonly string highpassIntensityFrequencyCurveLabel = "Frequency Curve";
        public static readonly string highpassIntensityFrequencyCurveTooltip = "Curve of the highpass frequency over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        public static readonly string highpassIntensityAmountLabel = "Intensity Amount";
        public static readonly string highpassIntensityAmountTooltip = 
            $"Changes the sound over intensity.\n\nUse on for example physics sounds where you pass the velocity with a {nameof(SoundParameterIntensity)}." + EditorTrial.trialTooltip;

        public static readonly string highpassIntensityAmountRolloffLabel = "Amount Rolloff";
        public static readonly string highpassIntensityAmountRolloffTooltip = "The power of the rolloff.\n\n0 is linear." + EditorTrial.trialTooltip;

        public static readonly string highpassIntensityAmountStrengthLabel = "Amount Strength";
        public static readonly string highpassIntensityAmountStrengthTooltip = "How much effect the intensity should have." + EditorTrial.trialTooltip;

        public static readonly string highpassIntensityAmountCurveLabel = "Amount Curve";
        public static readonly string highpassIntensityAmountCurveTooltip = "Curve of the highpass amount over intensity.\n\nFrom 0 (soft) to 1 (hard)." + EditorTrial.trialTooltip;

        // Bottom
        public static readonly string showPreviewCurvesLabel = "Show Preview Curves";
        public static readonly string showPreviewCurvesTooltip = "Toggles showing the preview curves." + EditorTrial.trialTooltip;

        public static readonly string resetSettingsLabel = "Reset Options";
        public static readonly string resetSettingsTooltip = $"Resets the {nameof(NameOf.SoundContainer)} to the default options." + EditorTrial.trialTooltip;

        public static readonly string resetAllLabel = "Reset All";
        public static readonly string resetAllTooltip = $"Resets the {nameof(NameOf.SoundContainer)} and the {nameof(AudioClip)}s to the default settings." + EditorTrial.trialTooltip;
    }
}
#endif