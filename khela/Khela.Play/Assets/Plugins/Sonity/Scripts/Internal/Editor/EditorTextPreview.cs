// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if UNITY_EDITOR


namespace Sonity.Internal {

    public static class EditorTextPreview {

        public static readonly string soundContainerPlayLabel = "Play";
        public static readonly string soundContainerPlayTooltip =
            $"Previews the {nameof(NameOf.SoundContainer)}." + "\n" +
            "\n" +
            $"Does not work if Unity cannot build the project, or if the game is paused." + "\n" +
            "\n" +
            $"The default shortcut is Ctrl+Q." + EditorTrial.trialTooltip;

        public static readonly string soundEventBasePlayLabel = "Play";
        public static readonly string soundEventBasePlayTooltip =
            $"Previews the {nameof(NameOf.SoundEvent)}." + "\n" +
            "\n" +
            $"Does not work if Unity cannot build the project, or if the game is paused." + "\n" +
            "\n" +
            $"Preview doesn't play more than one level of TriggerOnPlay/Stop/Tail at the moment (there is full functionality ingame)." + "\n" +
            "\n" +
            $"The default shortcut is Ctrl+Q." + EditorTrial.trialTooltip;

        public static readonly string soundEventSoundTagPlayLabel = "Play";
        public static readonly string soundEventSoundTagPlayTooltip =
            $"Previews the {nameof(NameOf.SoundEvent)} with {nameof(NameOf.SoundTag)}." + "\n" +
            "\n" +
            $"Does not work if Unity cannot build the project, or if the game is paused." + "\n" +
            "\n" +
            $"The default shortcut is Ctrl+Q." + EditorTrial.trialTooltip;

        public static readonly string stopLabel = "Stop";
        public static readonly string stopTooltip = 
            "Press two times to skip the fade out." + "\n" +
            "\n" +
            $"The default shortcut is Ctrl+W." + EditorTrial.trialTooltip;

        public static readonly string resetLabel = "Reset";
        public static readonly string resetTooltip = "Resets the preview settings." + EditorTrial.trialTooltip;

        public static readonly string intensityLabel = "Intensity";
        public static readonly string intensityTooltip = $"Controls the intensity value of the played {nameof(NameOf.SoundContainer)}s." + EditorTrial.trialTooltip;

        public static readonly string audioMixerGroupLabel = "AudioMixerGroup";
        public static readonly string audioMixerGroupTooltip = $"Only used for preview." + EditorTrial.trialTooltip;

        public static readonly string boxFront = "Front";
        public static readonly string boxBack = "Back";
        public static readonly string boxLeft = "Left";
        public static readonly string boxRight = "Right";
    }
}
#endif