// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if UNITY_EDITOR

using UnityEngine;

namespace Sonity.Internal {

    public class EditorTextSoundTrigger {

        public static readonly string soundTriggerTooltip =
            $"{nameof(NameOf.SoundTrigger)} is a component used for easily playing/stopping {nameof(NameOf.SoundEvent)}s on callbacks built into Unity like Enable, Disable, OnCollisionEnter etc." + "\n" +
            "\n" +
            $"They contain {nameof(NameOf.SoundEvent)}s with modifiers and triggers which decide when it should play or stop." + "\n" +
            "\n" +
            $"{nameof(NameOf.SoundTrigger)}s also have a radius handle, which is visually editable in the scene viewport for easy adjustment of how far {nameof(NameOf.SoundEvent)}s should be heard." + "\n" +
            "\n" +
            $"All {nameof(NameOf.SoundTrigger)} components are multi-object editable." + EditorTrial.trialTooltip;

        public static readonly string distanceRadiusLabel = "Distance Radius";
        public static readonly string distanceRadiusTooltip = $"Distance of the {nameof(NameOf.SoundEvent)} (how far it should be heard)." + EditorTrial.trialTooltip;

        // Warning
        public static readonly string radiusHandleWarningNoDistance = $"No {nameof(NameOf.SoundContainer)} has distance enabled";
        public static readonly string radiusHandleWarningNoSoundEvents = $"No {nameof(NameOf.SoundEvent)}s";

        public static readonly string soundEventLabel = $"{nameof(NameOf.SoundEvent)}";
        public static readonly string soundEventTooltip = $"The {nameof(NameOf.SoundEvent)} which is played" + EditorTrial.trialTooltip;

        // On Basic
        public static readonly string onBasicLabel = $"Basic";
        public static readonly string onBasicTooltip = $"";

        public static readonly string onEnableLabel = $"On Enable";
        public static readonly string onEnableTooltip = $"";

        public static readonly string onDisableLabel = $"On Disable";
        public static readonly string onDisableTooltip = $"";

        public static readonly string onStartLabel = $"On Start";
        public static readonly string onStartTooltip = $"";

        public static readonly string onDestroyLabel = $"On Destroy";
        public static readonly string onDestroyTooltip = $"";

        // On Trigger
        public static readonly string onTriggerLabel = $"Trigger";
        public static readonly string onTriggerTooltip = $"";

        public static readonly string onTriggerEnterLabel = $"On Trigger Enter";
        public static readonly string onTriggerEnterTooltip = $"";

        public static readonly string onTriggerExitLabel = $"On Trigger Exit";
        public static readonly string onTriggerExitTooltip = $"";

        public static readonly string onTriggerEnter2DLabel = $"On Trigger Enter 2D";
        public static readonly string onTriggerEnter2DTooltip = $"";

        public static readonly string onTriggerExit2DLabel = $"On Trigger Exit 2D";
        public static readonly string onTriggerExit2DTooltip = $"";

        public static readonly string triggerTagLabel = $"Tag";
        public static readonly string triggerTagTooltip = $"If enabled, the {nameof(NameOf.SoundEvent)} will only play if the triggering object has a tag matching the selected tags." + EditorTrial.trialTooltip;

        // On Collision
        public static readonly string onCollisionLabel = $"Collision";
        public static readonly string onCollisionTooltip = $"";

        public static readonly string onCollisionEnterLabel = $"On Collision Enter";
        public static readonly string onCollisionEnterTooltip = $"";

        public static readonly string onCollisionExitLabel = $"On Collision Exit";
        public static readonly string onCollisionExitTooltip = $"";

        public static readonly string onCollisionEnter2DLabel = $"On Collision Enter 2D";
        public static readonly string onCollisionEnter2DTooltip = $"";

        public static readonly string onCollisionExit2DLabel = $"On Collision Exit 2D";
        public static readonly string onCollisionExit2DTooltip = $"";

        public static readonly string velocityToIntensityLabel = $"Velocity to Intensity";
        public static readonly string velocityToIntensityTooltip = $"If enabled, the velocity magnitude will be passed as an intensity parameter.";

        public static readonly string collisionTagLabel = $"Tag";
        public static readonly string collisionTagTooltip = $"If enabled, the {nameof(NameOf.SoundEvent)} will only play if the collision object has a tag matching the selected tags." + EditorTrial.trialTooltip;

        // On Mouse
        public static readonly string onMouseLabel = $"Mouse";
        public static readonly string onMouseTooltip = $"";

        public static readonly string onMouseEnterLabel = $"On Mouse Enter";
        public static readonly string onMouseEnterTooltip = $"";

        public static readonly string onMouseExitLabel = $"On Mouse Exit";
        public static readonly string onMouseExitTooltip = $"";

        public static readonly string onMouseDownLabel = $"On Mouse Down";
        public static readonly string onMouseDownTooltip = $"";

        public static readonly string onMouseUpLabel = $"On Mouse Up";
        public static readonly string onMouseUpTooltip = $"";

        // Warning
        public static readonly string warningRigidbody3D = $"{nameof(Rigidbody)} is required by the Triggers";
        public static readonly string warningCollider3D = $"{nameof(Collider)} is required by the Triggers";

        public static readonly string warningRigidbody2D = $"{nameof(Rigidbody2D)} is required by the Triggers";
        public static readonly string warningCollider2D = $"{nameof(Collider2D)} is required by the Triggers";
    }
}
#endif