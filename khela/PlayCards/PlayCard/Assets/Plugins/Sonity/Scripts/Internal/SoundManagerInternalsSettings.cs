// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

using UnityEngine;
using System;
using UnityEngine.Audio; // Used with SONITY_ENABLE_ADDRESSABLE_AUDIOMIXER
using System.Collections;
#if SONITY_ENABLE_ADDRESSABLE_AUDIOMIXER
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.AddressableAssets;
#endif

namespace Sonity.Internal {
    
    [Serializable]
    public class SoundManagerInternalsSettings {

        public bool settingExpandBase = true;
        public bool settingsExpandAdvanced = false;

        public bool disablePlayingSounds = false;

        public bool asyncStartup = false;

        // Steam Audio
        public bool steamAudioIntegrationEnable = false;
        public bool steamAudioSpatializeEnabledDefault = true;

        // Entity Sound
        public bool entitySoundIntegrationEnable = false;

        // Playmaker
        public bool playmakerIntegrationEnable = false;

        public SoundTimeScaleMode soundTimeScale = SoundTimeScaleMode.UnscaledTime;

        public bool globalPause = false;

        public float globalVolumeRatio = 1f;
        // Decibel only used in the editor
        public float globalVolumeDecibel = 0f;

        public bool volumeIncreaseEnable = false;

        public bool dontDestroyOnLoad = true;
        public bool debugWarnings = true;
        public bool debugInPlayMode = true;

        public bool GetShouldDebugWarnings() {
            if (debugWarnings) {
                if (Application.isPlaying) {
                    return debugInPlayMode;
                } else {
                    return true;
                }
            }
            return false;
        }

#if UNITY_EDITOR
        public bool guiWarnings = true;
        public bool GetShouldDebugGuiWarnings() {
            return guiWarnings;
        }
#endif

        public bool addressableAudioMixerUse = false;
        public bool addressableAudioMixerAsyncLoadInEditor = false;

        public bool GetAddressableAudioMixerAsyncLoad() {
            return asyncStartup && !(Application.isEditor && !addressableAudioMixerAsyncLoadInEditor);
        }

#if SONITY_ENABLE_ADDRESSABLE_AUDIOMIXER

        public IEnumerator LoadAddressableAudioMixerAsync() {
            if (!addressableAudioMixerUse) {
                Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer is disabled but still is trying to be used.");
                yield break;
            }

            if (addressableAudioMixerAsset != null) {
                yield break;
            }

            if (addressableAudioMixerReference == null) {
                Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer AssetReference is null.");
                yield break;
            }

            if (addressableAudioMixerReference.RuntimeKeyIsValid() == false) {
                Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer AssetReference runtime key is not valid.");
                yield break;
            }

            AsyncOperationHandle<AudioMixer> audioMixerLoadHandle = Addressables.LoadAssetAsync<AudioMixer>(addressableAudioMixerReference);
            while (!audioMixerLoadHandle.IsDone) {
                yield return null;
            }

            if (audioMixerLoadHandle.Status == AsyncOperationStatus.Succeeded) {
                addressableAudioMixerAsset = audioMixerLoadHandle.Result;
            } else if (audioMixerLoadHandle.Status == AsyncOperationStatus.Failed) {
                Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer loading: async operation status failed.");
            } else if (audioMixerLoadHandle.Status == AsyncOperationStatus.None) {
                Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer loading: async operation status none.");
            }
        }

        public UnityEngine.AddressableAssets.AssetReference addressableAudioMixerReference;
        private AudioMixer addressableAudioMixerAsset;

        public AudioMixer GetAddressableAudioMixer() {
            if (addressableAudioMixerUse) {
                if (addressableAudioMixerAsset != null) {
                    return addressableAudioMixerAsset;
                } else {
                    Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer AssetReference is null.");
                }
            } else {
                Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer is disabled but still is trying to be used.");
            }
            return null;
        }

        public AudioMixerGroup AddressableAudioMixerGroupConvert(AudioMixerGroup audioMixerGroup) {
            // Ignore SoundEvents without assigned AudioMixerGroups
            if (audioMixerGroup != null) {
                // Find the group with the same name in the Addressable AudioMixer
                if (addressableAudioMixerReference != null && addressableAudioMixerReference.RuntimeKeyIsValid()) {
                    return SoundManagerBase.Instance.Internals.settings.GetAddressableAudioMixer().FindMatchingGroups(audioMixerGroup.name)[0];
                } else {
                    Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer AssetReference is null.");
                }
            }
            return audioMixerGroup;
        }

        public void LoadAddressableAudioMixer() {
            if (addressableAudioMixerUse) {
                if (addressableAudioMixerAsset == null) {
                    if (addressableAudioMixerReference != null && addressableAudioMixerReference.RuntimeKeyIsValid()) {
                        AsyncOperationHandle<AudioMixer> audioMixerLoadHandle = Addressables.LoadAssetAsync<AudioMixer>(addressableAudioMixerReference);
                        audioMixerLoadHandle.WaitForCompletion();
                        if (audioMixerLoadHandle.Status == AsyncOperationStatus.Succeeded) {
                            if (audioMixerLoadHandle.Result != null) {
                                addressableAudioMixerAsset = audioMixerLoadHandle.Result;
                            } else {
                                Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer is loaded but null.");
                            }
                        } else {
                            Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer AssetReference {addressableAudioMixerReference} failed to load.");
                        }
                    } else {
                        Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer AssetReference is null.");
                    }
                }
            } else {
                Debug.LogError($"Sonity.{nameof(NameOf.SoundManager)}: Addressable AudioMixer is disabled but still is trying to be used.");
            }
        }
#endif

        public SoundTagBase globalSoundTag;
        public float distanceScale = 1f;
        public bool overrideListenerDistance = false;
        public float overrideListenerDistanceAmount = 100f;
        public bool speedOfSoundEnabled = false;
        public float speedOfSoundScale = 1f;

        /// <summary>
        /// Returns the Sound of Speed delay in seconds
        /// Distance is the unscaled distance between the <see cref="AudioListener"/> and the <see cref="Voice"/>
        /// </summary>
        public float GetSpeedOfSoundDelay(float distance) {
            if (speedOfSoundEnabled) {
                // Base value is 340 unity units per second. 1/340 = 0.00294117647058823529
                return distance * 0.002941f * speedOfSoundScale;
            } else {
                return 0f;
            }
        }

        public int voicePreload = 32;
        public int voiceLimit = 32;
        public float voiceStopTime = 5f;
        public int voiceEffectLimit = 32;
    }
}