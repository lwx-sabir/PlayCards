using System;

namespace PlayCard.Haptics
{
    /// <summary>
    /// A named, multi-pulse haptic pattern. Register once via <see cref="Haptic.RegisterPattern"/>, then play it by
    /// ID. Each platform realises it natively: iOS plays a CoreHaptics pattern honouring per-event intensity +
    /// sharpness + timing; Android flattens it into a single <c>VibrationEffect.createWaveform</c>; WebGL turns it
    /// into a <c>navigator.vibrate</c> timing array (no amplitude).
    /// </summary>
    [Serializable]
    public class HapticPattern
    {
        public string ID;
        public HapticEvent[] Pattern;

        public HapticPattern(string id, HapticEvent[] pattern)
        {
            ID = id;
            Pattern = pattern;
        }
    }
}
