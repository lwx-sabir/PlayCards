// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

using UnityEngine;
using UnityEngine.Audio;

namespace SonityTemplate {

    /// <summary>
    /// Template of a singleton <see cref="AudioMixer"/> volume controller.
    /// The default settings work with the provided "TemplateAudioMixer.mixer".
    /// Add to a GameObject in the scene and use like this:
    /// SonityTemplate.TemplateSoundVolumeManager.Instance.SetVolumeMaster(1f);
    /// </summary>
    [AddComponentMenu("Sonity/Template - Sound Volume Manager")]
    public class TemplateSoundVolumeManager : MonoBehaviour {

        public AudioMixer audioMixer;

        // This sets the volume range in decibel scaled from 0-1
        private float volumeRangeLowestDecibel = -40f;

        // The names need to match the names of the exposed parameters in the mixer
        private const string parameterNameMaster = "Master_Volume";
        private const string parameterNameMUS = "MUS_Volume";
        private const string parameterNameSFX = "SFX_Volume";
        private const string parameterNameAMB = "AMB_Volume";
        private const string parameterNameUI = "UI_Volume";
        private const string parameterNameVO = "VO_Volume";

        /// <summary>
        /// Set <see cref="AudioMixer"/> volumes
        /// Input is range 0 to 1
        /// Its converted to a -40 to -0dB scale (the volume range is assignable with volumeRangeLowestDecibel)
        /// 0 will clamp to -80dB (-infinity in the <see cref="AudioMixer"/>)
        /// </summary>
        private void SetVolume(float volumeLinear, string parameterName) {
            if (audioMixer == null) {
#if UNITY_EDITOR
                Debug.LogWarning($"You need to assign an {nameof(AudioMixer)} to set the volume.", this);
#endif
                return;
            }
            volumeLinear = Mathf.Clamp(volumeLinear, 0f, 1f);
            // Invert and convert to dB
            volumeLinear = (1f - volumeLinear) * volumeRangeLowestDecibel;
            // Snap to -infinity
            if (volumeLinear <= volumeRangeLowestDecibel) {
                volumeLinear = -80f;
            }
            // Set volume
            audioMixer.SetFloat(parameterName, volumeLinear);
        }

        public void SetVolumeMaster(float volumeLinear) {
            SetVolume(volumeLinear, parameterNameMaster);
        }

        public void SetVolumeMUS(float volumeLinear) {
            SetVolume(volumeLinear, parameterNameMUS);
        }

        public void SetVolumeSFX(float volumeLinear) {
            SetVolume(volumeLinear, parameterNameSFX);
        }

        public void SetVolumeAMB(float volumeLinear) {
            SetVolume(volumeLinear, parameterNameAMB);
        }

        public void SetVolumeUI(float volumeLinear) {
            SetVolume(volumeLinear, parameterNameUI);
        }

        public void SetVolumeVO(float volumeLinear) {
            SetVolume(volumeLinear, parameterNameVO);
        }

        public float VolumeMaster {
            set => SetVolumeMaster(value);
        }

        public float VolumeMUS {
            set => SetVolumeMUS(value);
        }

        public float VolumeSFX {
            set => SetVolumeSFX(value);
        }

        public float VolumeAMB {
            set => SetVolumeAMB(value);
        }

        public float VolumeUI {
            set => SetVolumeUI(value);
        }

        public float VolumeVO {
            set => SetVolumeVO(value);
        }

        // Singleton Instance
        public bool useDontDestroyOnLoad = true;
        private bool isGoingToDelete;
#if UNITY_EDITOR
        private bool debugInstanceDestroyed = true;
#endif

        public static TemplateSoundVolumeManager Instance {
            get;
            private set;
        }

        private void Awake() {
            InstanceCheck();
            if (useDontDestroyOnLoad && !isGoingToDelete) {
                // DontDestroyOnLoad only works for root GameObjects
                gameObject.transform.parent = null;
                DontDestroyOnLoad(gameObject);
            }
        }

        // Checks if there are multiple instances this script, if so it destroys one of them
        private void InstanceCheck() {
            if (Instance == null) {
                Instance = this;
            } else if (Instance != this) {
#if UNITY_EDITOR
                if (debugInstanceDestroyed) {
                    Debug.LogWarning($"There can only be one {nameof(TemplateSoundVolumeManager)} instance per scene.", this);
                }
#endif
                // So that it does not run the rest of the Awake and Update code
                isGoingToDelete = true;
                if (Application.isPlaying) {
                    Destroy(gameObject);
                }
            }
        }
    }
}