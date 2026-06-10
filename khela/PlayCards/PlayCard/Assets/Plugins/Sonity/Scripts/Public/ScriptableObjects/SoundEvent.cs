// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

using UnityEngine;
using System;
using Sonity.Internal;

namespace Sonity {

    /// <summary>
    /// <see cref="SoundEventBase">SoundEvents</see> are what you play in Sonity.
    /// They contain <see cref="SoundContainerBase">SoundContainer</see> and options of how the sound should be played.
    /// All <see cref="SoundEventBase">SoundEvents</see> are multi-object editable.
    /// </summary>
    [Serializable]
    [CreateAssetMenu(fileName = "_SE", menuName = "Sonity 🔊/SoundEvent", order = 101)] // Having a big gap in indexes creates dividers
    public class SoundEvent : SoundEventBase {

        private bool SoundManagerIsNull() {
            if (SoundManagerBase.Instance == null) {
                if (!ApplicationQuitting.GetApplicationIsQuitting()) {
                    Debug.LogWarning($"Sonity.{nameof(NameOf.SoundManager)} is null. Add one to the scene.");
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the <see cref="Transform"/> position
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/> (can follow positon)
        /// </param>
        public void Play(Transform owner) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.Play(this, owner);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the <see cref="Transform"/> position with the Local <see cref="SoundTagBase">SoundTag</see>
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/> (can follow positon)
        /// </param>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        public void Play(Transform owner, SoundTagBase localSoundTag) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.Play(this, owner, localSoundTag);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> at the <see cref="Transform"/> position
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/> (can follow positon)
        /// </param>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void Play(Transform owner, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.Play(this, owner, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> at the <see cref="Transform"/> position with the Local <see cref="SoundTagBase">SoundTag</see>
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/> (can follow positon)
        /// </param>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void Play(Transform owner, SoundTagBase localSoundTag, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.Play(this, owner, localSoundTag, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the <see cref="Transform"/> position with another <see cref="Transform"/> owner
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='position'>
        /// The position <see cref="Transform"/> (can follow position)
        /// </param>
        public void PlayAtPosition(Transform owner, Transform position) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PlayAtPosition(this, owner, position);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the <see cref="Transform"/> position with another <see cref="Transform"/> owner with the Local <see cref="SoundTagBase">SoundTag</see>
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='position'>
        /// The position <see cref="Transform"/> (can follow position)
        /// </param>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        public void PlayAtPosition(Transform owner, Transform position, SoundTagBase localSoundTag) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PlayAtPosition(this, owner, position, localSoundTag);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> at the <see cref="Transform"/> position with another <see cref="Transform"/> owner
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='position'>
        /// The position <see cref="Transform"/> (can follow position)
        /// </param>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void PlayAtPosition(Transform owner, Transform position, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PlayAtPosition(this, owner, position, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> at the <see cref="Transform"/> position with another <see cref="Transform"/> owner with the Local <see cref="SoundTagBase">SoundTag</see>
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='position'>
        /// The position <see cref="Transform"/> (can follow position)
        /// </param>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        public void PlayAtPosition(Transform owner, Transform position, SoundTagBase localSoundTag, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PlayAtPosition(this, owner, position, localSoundTag, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the <see cref="Vector3"/> position
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='position'>
        /// The position <see cref="Vector3"/> (can't follow position)
        /// </param>
        public void PlayAtPosition(Transform owner, Vector3 position) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PlayAtPosition(this, owner, position);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the <see cref="Vector3"/> position with the Local <see cref="SoundTagBase">SoundTag</see>
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='position'>
        /// The position <see cref="Vector3"/> (can't follow position)
        /// </param>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        public void PlayAtPosition(Transform owner, Vector3 position, SoundTagBase localSoundTag) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PlayAtPosition(this, owner, position, localSoundTag);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> at the <see cref="Vector3"/> position
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='position'>
        /// The position <see cref="Vector3"/> (can't follow position)
        /// </param>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void PlayAtPosition(Transform owner, Vector3 position, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PlayAtPosition(this, owner, position, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> at the <see cref="Vector3"/> position with the Local <see cref="SoundTagBase">SoundTag</see>
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='position'>
        /// The position <see cref="Vector3"/> (can't follow position)
        /// </param>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        public void PlayAtPosition(Transform owner, Vector3 position, SoundTagBase localSoundTag, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PlayAtPosition(this, owner, position, localSoundTag, soundParameters);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> with the owner <see cref="Transform"/>
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void Stop(Transform owner, bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.Stop(this, owner, OwnerPlayType.Normal, allowFadeOut);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> played at the position <see cref="Transform"/>
        /// </summary>
        /// <param name='position'>
        /// The position <see cref="Transform"/>
        /// </param>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void StopAtPosition(Transform position, bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.StopAtPosition(this, position, allowFadeOut);
            }
        }

        /// <summary>
        /// Stops all <see cref="SoundEventBase">SoundEvents</see> with the owner <see cref="Transform"/>
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void StopAllAtOwner(Transform owner, bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.StopAllAtOwner(owner, allowFadeOut);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> with the owner <see cref="Transform"/> allowing fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        public void StopAllowFadeOut(Transform owner) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.Stop(this, owner, OwnerPlayType.Normal, true);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> played at the position <see cref="Transform"/> allowing fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='position'>
        /// The position <see cref="Transform"/>
        /// </param>
        public void StopAtPositionAllowFadeOut(Transform position) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.StopAtPosition(this, position, true);
            }
        }

        /// <summary>
        /// Stops all <see cref="SoundEventBase">SoundEvents</see> with the owner <see cref="Transform"/> allowing fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        public void StopAllAtOwnerAllowFadeOut(Transform owner) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.StopAllAtOwner(owner, true);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> with the owner <see cref="Transform"/> without fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        public void StopImmediate(Transform owner) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.Stop(this, owner, OwnerPlayType.Normal, false);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> played at the position <see cref="Transform"/> without fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='position'>
        /// The position <see cref="Transform"/>
        /// </param>
        public void StopAtPositionImmediate(Transform position) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.StopAtPosition(this, position, false);
            }
        }

        /// <summary>
        /// Stops all <see cref="SoundEventBase">SoundEvents</see> with the owner <see cref="Transform"/> without fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        public void StopAllAtOwnerImmediate(Transform owner) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.StopAllAtOwner(owner, false);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> everywhere
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void StopEverywhere(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.StopEverywhere(this, allowFadeOut);
            }
        }

        /// <summary>
        /// Stops all the <see cref="SoundEventBase">SoundEvents</see>
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvents</see> should be allowed to fade out. Otherwise they are going to be stopped immediately
        /// </param>
        public void StopEverything(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.StopEverything(allowFadeOut);
            }
        }

        /// <summary>
        /// Pauses the <see cref="SoundEventBase">SoundEvent</see> with the owner <see cref="Transform"/> locally
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void Pause(Transform owner, bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.Pause(this, owner, OwnerPlayType.Normal, forcePause);
            }
        }

        /// <summary>
        /// Unpauses the <see cref="SoundEventBase">SoundEvent</see> with the owner <see cref="Transform"/> locally
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        public void Unpause(Transform owner) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.Unpause(this, owner, OwnerPlayType.Normal);
            }
        }

        /// <summary>
        /// Pauses all <see cref="SoundEventBase">SoundEvents</see> with the owner <see cref="Transform"/> locally
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void PauseAllAtOwner(Transform owner, bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PauseAllAtOwner(owner, forcePause);
            }
        }

        /// <summary>
        /// Unpauses all <see cref="SoundEventBase">SoundEvents</see> with the owner <see cref="Transform"/> locally
        /// </summary>
        /// <param name='owner'>
        /// The owner <see cref="Transform"/>
        /// </param>
        public void UnpauseAllAtOwner(Transform owner) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UnpauseAllAtOwner(owner);
            }
        }

        /// <summary>
        /// Pauses the <see cref="SoundEventBase">SoundEvent</see> everywhere locally
        /// </summary>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void PauseEverywhere(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PauseEverywhere(this, forcePause);
            }
        }

        /// <summary>
        /// Unpauses the <see cref="SoundEventBase">SoundEvent</see> everywhere locally
        /// </summary>
        public void UnpauseEverywhere() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UnpauseEverywhere(this);
            }
        }

        /// <summary>
        /// Pauses the all <see cref="SoundEventBase">SoundEvents</see> locally
        /// </summary>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void PauseEverything(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.PauseEverything(forcePause);
            }
        }

        /// <summary>
        /// Unpauses the all <see cref="SoundEventBase">SoundEvents</see> locally
        /// </summary>
        public void UnpauseEverything() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UnpauseEverything();
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with the UI <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// </summary>
        public void UIPlay() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlay(this);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with the Local <see cref="SoundTagBase">SoundTag</see> with the UI <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// </summary>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        public void UIPlay(SoundTagBase localSoundTag) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlay(this, localSoundTag);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> with the UI <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// </summary>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void UIPlay(params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlay(this, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> with the Local <see cref="SoundTagBase">SoundTag</see> with the UI <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// </summary>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void UIPlay(SoundTagBase localSoundTag, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlay(this, localSoundTag, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the position with the UI <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='position'>
        /// The position <see cref="Vector3"/> (can't follow position)
        /// </param>
        public void UIPlayAtPosition(Vector3 position) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlayAtPosition(this, position);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the position with the UI <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='position'>
        /// The position <see cref="Transform"/> (can follow position)
        /// </param>
        public void UIPlayAtPosition(Transform position) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlayAtPosition(this, position);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> at the UI <see cref="Transform"/>
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void UIStop(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIStop(this, allowFadeOut);
            }
        }

        /// <summary>
        /// Stops all <see cref="SoundEventBase">SoundEvents</see> at the UI <see cref="Transform"/>
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void UIStopAll(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIStopAll(allowFadeOut);
            }
        }

        /// <summary>
        /// Pauses the <see cref="SoundEventBase">SoundEvent</see> at the UI <see cref="Transform"/> locally
        /// </summary>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void UIPause(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPause(this, forcePause);
            }
        }

        /// <summary>
        /// Unpauses the <see cref="SoundEventBase">SoundEvent</see> at the UI <see cref="Transform"/> locally
        /// </summary>
        public void UIUnpause() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIUnpause(this);
            }
        }

        /// <summary>
        /// Pauses all <see cref="SoundEventBase">SoundEvents</see> at the UI <see cref="Transform"/> locally
        /// </summary>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void UIPauseAll(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPauseAll(forcePause);
            }
        }

        /// <summary>
        /// Unpauses all <see cref="SoundEventBase">SoundEvents</see> at the UI <see cref="Transform"/> locally
        /// </summary>
        public void UIUnpauseAll() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIUnpauseAll();
            }
        }

        /// <summary>
        /// <para> Uses the UI owner Transform </para>
        /// <para> If playing it returns <see cref="SoundEventState.Playing"/> </para> 
        /// <para> If paused either locally or globally it returns <see cref="SoundEventState.Paused"/> </para>
        /// <para> If not playing, but it is delayed it returns <see cref="SoundEventState.Delayed"/> </para> 
        /// <para> If not playing and it is not delayed it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// <para> If the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// </summary>
        /// <returns> Returns <see cref="SoundEventState"/> of the <see cref="SoundEventBase">SoundEvents</see> <see cref="SoundEventInstance"/> </returns>
        public SoundEventState UIGetSoundEventState() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetSoundEventState(this);
            }
            return SoundEventState.NotPlaying;
        }

        /// <summary>
        /// <para> Uses the UI owner Transform </para>
        /// <para> Returns the length (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns <see cref="Mathf.Infinity"/> if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns <see cref="Mathf.Infinity"/> if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Length in seconds </returns>
        public float UIGetLastPlayedClipLength(bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetLastPlayedClipLength(this, pitchSpeed);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the UI owner Transform </para>
        /// <para> Returns the current time (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Time in seconds </returns>
        public float UIGetLastPlayedClipTimeSeconds(bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetLastPlayedClipTimeSeconds(this, pitchSpeed);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the UI owner Transform </para>
        /// <para> Returns the current time (in range 0 to 1) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <returns> Time ratio from 0 to 1 </returns>
        public float UIGetLastPlayedClipTimeRatio() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetLastPlayedClipTimeRatio(this);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the UI owner Transform </para>
        /// <para> Returns the time (in seconds) since the <see cref="SoundEventBase">SoundEvent</see> was played </para>
        /// <para> Is calculated using the time scale selected in the <see cref="SoundManagerBase">SoundManager</see> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <returns> Time in seconds </returns>
        public float UIGetTimePlayed() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetTimePlayed(this);
            }
            return 0;
        }

        /// <summary>
        /// Returns the owner <see cref="Transform"/> used by UIPlay() etc
        /// </summary>
        /// <returns> The <see cref="Transform"/> used by UIPlay() etc </returns>
        public Transform UIGetTransform() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetTransform();
            }
            return null;
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the music <see cref="Transform"/>
        /// </summary>
        /// <param name="stopAllOtherMusic">
        /// If all other <see cref="SoundEventBase">SoundEvents</see> played at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> should be stopped
        /// </param>
        /// <param name="allowFadeOut">
        /// If the other stopped <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise they are going to be stopped immediately
        /// </param>
        public void MusicPlay(bool stopAllOtherMusic = true, bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPlay(this, stopAllOtherMusic, allowFadeOut);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the music <see cref="Transform"/>
        /// </summary>
        /// <param name="stopAllOtherMusic">
        /// If all other <see cref="SoundEventBase">SoundEvents</see> played at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> should be stopped
        /// </param>
        /// <param name="allowFadeOut">
        /// If the other stopped <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise they are going to be stopped immediately
        /// </param>
        /// <param name="soundParameters">
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void MusicPlay(bool stopAllOtherMusic = true, bool allowFadeOut = true, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPlay(this, stopAllOtherMusic, allowFadeOut, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the music <see cref="Transform"/> allowing fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name="stopAllOtherMusic">
        /// If all other <see cref="SoundEventBase">SoundEvents</see> played at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> should be stopped
        /// </param>
        public void MusicPlayAllowFadeOut(bool stopAllOtherMusic = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPlay(this, stopAllOtherMusic, true);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the music <see cref="Transform"/> without fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name="stopAllOtherMusic">
        /// If all other <see cref="SoundEventBase">SoundEvents</see> played at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> should be stopped
        /// </param>
        public void MusicPlayImmediate(bool stopAllOtherMusic = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPlay(this, stopAllOtherMusic, false);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> playing at the Music <see cref="Transform"/>
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the other stopped <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise they are going to be stopped immediately
        /// </param>
        public void MusicStop(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicStop(this, allowFadeOut);
            }
        }

        /// <summary>
        /// Stops all the <see cref="SoundEventBase">SoundEvents</see> playing at the Music <see cref="Transform"/>
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void MusicStopAll(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicStopAll(allowFadeOut);
            }
        }

        /// <summary>
        /// Pauses the <see cref="SoundEventBase">SoundEvent</see> playing at the Music <see cref="Transform"/> locally
        /// </summary>
        /// <param name='soundEvent'>
        /// The <see cref="SoundEventBase">SoundEvent</see> to pause
        /// </param>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void MusicPause(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPause(this, forcePause);
            }
        }

        /// <summary>
        /// Unpauses the <see cref="SoundEventBase">SoundEvent</see> at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> locally
        /// </summary>
        public void MusicUnpause() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicUnpause(this);
            }
        }

        /// <summary>
        /// Pauses all the <see cref="SoundEventBase">SoundEvents</see> playing at the Music <see cref="Transform"/> locally
        /// </summary>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void MusicPauseAll(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPauseAll(forcePause);
            }
        }

        /// <summary>
        /// Unpauses all the <see cref="SoundEventBase">SoundEvents</see> playing at the Music <see cref="Transform"/> locally
        /// </summary>
        public void MusicUnpauseAll() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicUnpauseAll();
            }
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> If playing it returns <see cref="SoundEventState.Playing"/> </para> 
        /// <para> If paused either locally or globally it returns <see cref="SoundEventState.Paused"/> </para>
        /// <para> If not playing, but it is delayed it returns <see cref="SoundEventState.Delayed"/> </para> 
        /// <para> If not playing and it is not delayed it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// <para> If the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// </summary>
        /// <returns> Returns <see cref="SoundEventState"/> of the <see cref="SoundEventBase">SoundEvents</see> <see cref="SoundEventInstance"/> </returns>
        public SoundEventState MusicGetSoundEventState() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetSoundEventState(this);
            }
            return SoundEventState.NotPlaying;
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> Returns the length (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns <see cref="Mathf.Infinity"/> if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns <see cref="Mathf.Infinity"/> if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Length in seconds </returns>
        public float MusicGetLastPlayedClipLength(bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetLastPlayedClipLength(this, pitchSpeed);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> Returns the current time (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Time in seconds </returns>
        public float MusicGetLastPlayedClipTimeSeconds(bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetLastPlayedClipTimeSeconds(this, pitchSpeed);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> Returns the current time (in range 0 to 1) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <returns> Time ratio from 0 to 1 </returns>
        public float MusicGetLastPlayedClipTimeRatio() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetLastPlayedClipTimeRatio(this);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> Returns the time (in seconds) since the <see cref="SoundEventBase">SoundEvent</see> was played </para>
        /// <para> Is calculated using the time scale selected in the <see cref="SoundManagerBase">SoundManager</see> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <returns> Time in seconds </returns>
        public float MusicGetTimePlayed() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetTimePlayed(this);
            }
            return 0;
        }

        /// <summary>
        /// Returns the owner <see cref="Transform"/> used by MusicPlay() etc
        /// </summary>
        /// <returns> The <see cref="Transform"/> used by MusicPlay() etc </returns>
        public Transform MusicGetTransform() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetTransform();
            }
            return null;
        }

        /// <summary>
        /// <para> If playing it returns <see cref="SoundEventState.Playing"/> </para> 
        /// <para> If paused either normally or globally it returns <see cref="SoundEventState.Paused"/> </para>
        /// <para> If not playing, but it is delayed it returns <see cref="SoundEventState.Delayed"/> </para> 
        /// <para> If not playing and it is not delayed it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// <para> If the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// </summary>
        /// <param name="owner"> The owner <see cref="Transform"/> </param>
        /// <returns> Returns <see cref="SoundEventState"/> of the <see cref="SoundEventBase">SoundEvents</see> <see cref="SoundEventInstance"/> </returns>
        public SoundEventState GetSoundEventState(Transform owner) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.GetSoundEventState(this, owner);
            }
            return SoundEventState.NotPlaying;
        }

        /// <summary>
        /// <para> Returns the length (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns <see cref="Mathf.Infinity"/> if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns <see cref="Mathf.Infinity"/> if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="owner"> The owner <see cref="Transform"/> </param>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Length in seconds </returns>
        public float GetLastPlayedClipLength(Transform owner, bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.GetLastPlayedClipLength(this, owner, pitchSpeed);
            }
            return 0f;
        }

        /// <summary>
        /// <para> Returns the current time (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="owner"> The owner <see cref="Transform"/> </param>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Time in seconds </returns>
        public float GetLastPlayedClipTimeSeconds(Transform owner, bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.GetLastPlayedClipTimeSeconds(this, owner, pitchSpeed);
            }
            return 0f;
        }

        /// <summary>
        /// <para> Returns the current time (in range 0 to 1) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <param name="owner"> The owner <see cref="Transform"/> </param>
        /// <returns> Time ratio from 0 to 1 </returns>
        public float GetLastPlayedClipTimeRatio(Transform owner) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.GetLastPlayedClipTimeRatio(this, owner);
            }
            return 0;
        }

        /// <summary>
        /// <para> Provides a block of spectrum data from <see cref="AudioSource"/>s </para>
        /// </summary>
        /// <param name="owner"> The owner <see cref="Transform"/> </param>
        /// <param name="samples"> The array to populate with audio samples. Its length must be a power of 2 </param>
        /// <param name="channel"> The channel to sample from </param>
        /// <param name="window"> The <see cref="FFTWindow"/> type to use when sampling </param>
        /// <param name="spectrumDataFrom"> Where to get the spectrum data from </param>
        public void GetSpectrumData(Transform owner, ref float[] samples, int channel, FFTWindow window, SpectrumDataFrom spectrumDataFrom) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.GetSpectrumData(this, owner, ref samples, channel, window, spectrumDataFrom);
            }
        }

        /// <summary>
        /// <para> Returns the last played <see cref="AudioSource"/> </para>
        /// <para> Note that the <see cref="AudioSource"/> might be stolen or reused for different Voices over time </para>
        /// </summary>
        /// <param name="owner"> The owner <see cref="Transform"/> </param>
        public AudioSource GetLastPlayedAudioSource(Transform owner) {
            if (!SoundManagerIsNull()) {
               return SoundManagerBase.Instance.Internals.GetLastPlayedAudioSource(this, owner);
            }
            return null;
        }

        /// <summary>
        /// <para> Returns the max length (in seconds) of the <see cref="SoundEventBase">SoundEvent</see> (calculated from the longest audioClip) </para>
        /// <para> Is scaled by the pitch of the <see cref="SoundEventBase">SoundEvent</see> and <see cref="SoundContainerBase">SoundContainer</see> </para>
        /// <para> Does not take into account random, intensity or parameter pitch </para>
        /// </summary>
        /// <returns> The max length in seconds </returns>
        public float GetMaxLength() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.GetMaxLength(this);
            }
            return 0f;
        }

        /// <summary>
        /// <para> Returns the time (in seconds) since the <see cref="SoundEventBase">SoundEvent</see> was played </para>
        /// <para> Is calculated using the time scale selected in the <see cref="SoundManagerBase">SoundManager</see> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <param name="owner"> The owner <see cref="Transform"/> </param>
        /// <returns> Time in seconds </returns>
        public float GetTimePlayed(Transform owner) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.GetTimePlayed(this, owner);
            }
            return 0f;
        }

        /// <summary>
        /// <para> Returns if any <see cref="SoundContainerBase">SoundContainers</see> in the <see cref="SoundEventBase">SoundEvent</see> is set to looping </para>
        /// </summary>
        /// <returns> If the <see cref="SoundEventBase">SoundEvent</see> contains a loop </returns>
        public bool GetContainsLoop() {
            return internals.GetContainsLoop();
        }

        /// <summary>
        /// Loads the audio data of any <see cref="AudioClip"/>s assigned to the <see cref="SoundContainerBase">SoundContainers</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </summary>
        new public void LoadAudioData() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.LoadAudioData(this);
            }
        }

        /// <summary>
        /// Unloads the audio data of any <see cref="AudioClip"/>s assigned to the <see cref="SoundContainerBase">SoundContainers</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </summary>
        new public void UnloadAudioData() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UnloadAudioData(this);
            }
        }

#if SONITY_ENABLE_LEGACY_FUNCTIONS_MUSIC_AND_2D

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with the 2D <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// </summary>
        public void Play2D() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlay(this);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with the Local <see cref="SoundTagBase">SoundTag</see> with the 2D <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// </summary>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        public void Play2D(SoundTagBase localSoundTag) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlay(this, localSoundTag);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> with the 2D <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// </summary>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void Play2D(params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlay(this, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> with <see cref="SoundParameterInternals"/> with the Local <see cref="SoundTagBase">SoundTag</see> with the 2D <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// </summary>
        /// <param name='localSoundTag'>
        /// The <see cref="SoundTagBase">SoundTag</see> which will determine the Local <see cref="SoundTagBase">SoundTag</see> of the <see cref="SoundEventBase">SoundEvent</see>
        /// </param>
        /// <param name='soundParameters'>
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void Play2D(SoundTagBase localSoundTag, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlay(this, localSoundTag, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the position with the 2D <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='position'>
        /// The position <see cref="Vector3"/> (can't follow position)
        /// </param>
        public void Play2DAtPosition(Vector3 position) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlayAtPosition(this, position);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the position with the 2D <see cref="Transform"/> as owner
        /// Useful to play e.g. UI or other 2D sounds without having to pass a <see cref="Transform"/>
        /// To make the sound 2D you still need to disable distance and set spatial blend to 0 in the <see cref="SoundContainerBase">SoundContainer</see>
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name='position'>
        /// The position <see cref="Transform"/> (can follow position)
        /// </param>
        public void Play2DAtPosition(Transform position) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPlayAtPosition(this, position);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> at the 2D <see cref="Transform"/>
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void Stop2D(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIStop(this, allowFadeOut);
            }
        }

        /// <summary>
        /// Stops all <see cref="SoundEventBase">SoundEvents</see> at the 2D <see cref="Transform"/>
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void StopAll2D(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIStopAll(allowFadeOut);
            }
        }

        /// <summary>
        /// Pauses the <see cref="SoundEventBase">SoundEvent</see> at the 2D <see cref="Transform"/> locally
        /// </summary>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void Pause2D(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPause(this, forcePause);
            }
        }

        /// <summary>
        /// Unpauses the <see cref="SoundEventBase">SoundEvent</see> at the 2D <see cref="Transform"/> locally
        /// </summary>
        public void Unpause2D() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIUnpause(this);
            }
        }

        /// <summary>
        /// Pauses all <see cref="SoundEventBase">SoundEvents</see> at the 2D <see cref="Transform"/> locally
        /// </summary>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void PauseAll2D(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIPauseAll(forcePause);
            }
        }

        /// <summary>
        /// Unpauses all <see cref="SoundEventBase">SoundEvents</see> at the 2D <see cref="Transform"/> locally
        /// </summary>
        public void UnpauseAll2D() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.UIUnpauseAll();
            }
        }

        /// <summary>
        /// <para> Uses the 2D owner Transform </para>
        /// <para> If playing it returns <see cref="SoundEventState.Playing"/> </para> 
        /// <para> If paused either locally or globally it returns <see cref="SoundEventState.Paused"/> </para>
        /// <para> If not playing, but it is delayed it returns <see cref="SoundEventState.Delayed"/> </para> 
        /// <para> If not playing and it is not delayed it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// <para> If the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// </summary>
        /// <returns> Returns <see cref="SoundEventState"/> of the <see cref="SoundEventBase">SoundEvents</see> <see cref="SoundEventInstance"/> </returns>
        public SoundEventState Get2DSoundEventState() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetSoundEventState(this);
            }
            return SoundEventState.NotPlaying;
        }

        /// <summary>
        /// <para> Uses the 2D owner Transform </para>
        /// <para> Returns the length (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Length in seconds </returns>
        public float Get2DLastPlayedClipLength(bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetLastPlayedClipLength(this, pitchSpeed);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the 2D owner Transform </para>
        /// <para> Returns the current time (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Time in seconds </returns>
        public float Get2DLastPlayedClipTimeSeconds(bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetLastPlayedClipTimeSeconds(this, pitchSpeed);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the 2D owner Transform </para>
        /// <para> Returns the current time (in range 0 to 1) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <returns> Time ratio from 0 to 1 </returns>
        public float Get2DLastPlayedClipTimeRatio() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetLastPlayedClipTimeRatio(this);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the 2D owner Transform </para>
        /// <para> Returns the time (in seconds) since the <see cref="SoundEventBase">SoundEvent</see> was played </para>
        /// <para> Is calculated using the time scale selected in the <see cref="SoundManagerBase">SoundManager</see> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <returns> Time in seconds </returns>
        public float Get2DTimePlayed() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetTimePlayed(this);
            }
            return 0;
        }

        /// <summary>
        /// Returns the owner <see cref="Transform"/> used by Play2D() etc
        /// </summary>
        /// <returns> The <see cref="Transform"/> used by Play2D() etc </returns>
        public Transform Get2DTransform() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.UIGetTransform();
            }
            return null;
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the music <see cref="Transform"/>
        /// </summary>
        /// <param name="stopAllOtherMusic">
        /// If all other <see cref="SoundEventBase">SoundEvents</see> played at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> should be stopped
        /// </param>
        /// <param name="allowFadeOut">
        /// If the other stopped <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise they are going to be stopped immediately
        /// </param>
        public void PlayMusic(bool stopAllOtherMusic = true, bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPlay(this, stopAllOtherMusic, allowFadeOut);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the music <see cref="Transform"/>
        /// </summary>
        /// <param name="stopAllOtherMusic">
        /// If all other <see cref="SoundEventBase">SoundEvents</see> played at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> should be stopped
        /// </param>
        /// <param name="allowFadeOut">
        /// If the other stopped <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise they are going to be stopped immediately
        /// </param>
        /// <param name="soundParameters">
        /// For example <see cref="SoundParameterVolumeDecibel"/> is used to modify how the <see cref="SoundEventBase">SoundEvent</see> is played
        /// </param>
        public void PlayMusic(bool stopAllOtherMusic = true, bool allowFadeOut = true, params SoundParameterInternals[] soundParameters) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPlay(this, stopAllOtherMusic, allowFadeOut, soundParameters);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the music <see cref="Transform"/> allowing fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name="stopAllOtherMusic">
        /// If all other <see cref="SoundEventBase">SoundEvents</see> played at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> should be stopped
        /// </param>
        public void PlayMusicAllowFadeOut(bool stopAllOtherMusic = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPlay(this, stopAllOtherMusic, true);
            }
        }

        /// <summary>
        /// Plays the <see cref="SoundEventBase">SoundEvent</see> at the music <see cref="Transform"/> without fade out
        /// Useful for <see cref="UnityEngine.Events.UnityEvent"/>s because it only has one parameter
        /// </summary>
        /// <param name="stopAllOtherMusic">
        /// If all other <see cref="SoundEventBase">SoundEvents</see> played at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> should be stopped
        /// </param>
        public void PlayMusicImmediate(bool stopAllOtherMusic = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPlay(this, stopAllOtherMusic, false);
            }
        }

        /// <summary>
        /// Stops the <see cref="SoundEventBase">SoundEvent</see> played with <see cref="PlayMusic()"/>
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the other stopped <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise they are going to be stopped immediately
        /// </param>
        public void StopMusic(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicStop(this, allowFadeOut);
            }
        }

        /// <summary>
        /// Stops all the <see cref="SoundEventBase">SoundEvents</see> played with MusicPlay
        /// </summary>
        /// <param name='allowFadeOut'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be allowed to fade out. Otherwise it is going to be stopped immediately
        /// </param>
        public void StopAllMusic(bool allowFadeOut = true) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicStopAll(allowFadeOut);
            }
        }

        /// <summary>
        /// Pauses the <see cref="SoundEventBase">SoundEvent</see> playing at the Music <see cref="Transform"/> locally
        /// </summary>
        /// <param name='soundEvent'>
        /// The <see cref="SoundEventBase">SoundEvent</see> to pause
        /// </param>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void PauseMusic(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPause(this, forcePause);
            }
        }

        /// <summary>
        /// Unpauses the <see cref="SoundEventBase">SoundEvent</see> at the <see cref="SoundManagerBase">SoundManagers</see> music <see cref="Transform"/> locally
        /// </summary>
        public void UnpauseMusic() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicUnpause(this);
            }
        }

        /// <summary>
        /// Pauses all the <see cref="SoundEventBase">SoundEvents</see> playing at the Music <see cref="Transform"/> locally
        /// </summary>
        /// <param name='forcePause'>
        /// If the <see cref="SoundEventBase">SoundEvent</see> should be paused even if it is set to "Ignore Local Pause"
        /// </param>
        public void PauseAllMusic(bool forcePause = false) {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicPauseAll(forcePause);
            }
        }

        /// <summary>
        /// Unpauses all the <see cref="SoundEventBase">SoundEvents</see> playing at the Music <see cref="Transform"/> locally
        /// </summary>
        public void UnpauseAllMusic() {
            if (!SoundManagerIsNull()) {
                SoundManagerBase.Instance.Internals.MusicUnpauseAll();
            }
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> If playing it returns <see cref="SoundEventState.Playing"/> </para> 
        /// <para> If paused either locally or globally it returns <see cref="SoundEventState.Paused"/> </para>
        /// <para> If not playing, but it is delayed it returns <see cref="SoundEventState.Delayed"/> </para> 
        /// <para> If not playing and it is not delayed it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// <para> If the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null it returns <see cref="SoundEventState.NotPlaying"/> </para> 
        /// </summary>
        /// <returns> Returns <see cref="SoundEventState"/> of the <see cref="SoundEventBase">SoundEvents</see> <see cref="SoundEventInstance"/> </returns>
        public SoundEventState GetMusicSoundEventState() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetSoundEventState(this);
            }
            return SoundEventState.NotPlaying;
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> Returns the length (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Length in seconds </returns>
        public float GetMusicLastPlayedClipLength(bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetLastPlayedClipLength(this, pitchSpeed);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> Returns the current time (in seconds) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// <para> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </para>
        /// </summary>
        /// <param name="pitchSpeed"> If it should be scaled by pitch. E.g. -12 semitones will be twice as long </param>
        /// <returns> Time in seconds </returns>
        public float GetMusicLastPlayedClipTimeSeconds(bool pitchSpeed) {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetLastPlayedClipTimeSeconds(this, pitchSpeed);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> Returns the current time (in range 0 to 1) of the <see cref="AudioClip"/> in the last played <see cref="AudioSource"/> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <returns> Time ratio from 0 to 1 </returns>
        public float GetMusicLastPlayedClipTimeRatio() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetLastPlayedClipTimeRatio(this);
            }
            return 0;
        }

        /// <summary>
        /// <para> Uses the Music owner Transform </para>
        /// <para> Returns the time (in seconds) since the <see cref="SoundEventBase">SoundEvent</see> was played </para>
        /// <para> Is calculated using the time scale selected in the <see cref="SoundManagerBase">SoundManager</see> </para>
        /// <para> Returns 0 if the <see cref="SoundEventInstance"/> is not playing </para>
        /// <para> Returns 0 if the <see cref="SoundEventBase">SoundEvent</see> or <see cref="Transform"/> is null </para>
        /// </summary>
        /// <returns> Time in seconds </returns>
        public float GetMusicTimePlayed() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetTimePlayed(this);
            }
            return 0;
        }

        /// <summary>
        /// Returns the owner <see cref="Transform"/> used by PlayMusic() etc
        /// </summary>
        /// <returns> The <see cref="Transform"/> used by PlayMusic() etc </returns>
        public Transform GetMusicTransform() {
            if (!SoundManagerIsNull()) {
                return SoundManagerBase.Instance.Internals.MusicGetTransform();
            }
            return null;
        }
#endif
    }
}