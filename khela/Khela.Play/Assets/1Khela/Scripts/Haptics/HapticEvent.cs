using System;

namespace PlayCard.Haptics
{
    /// <summary>
    /// One timed pulse inside a <see cref="HapticPattern"/>. <see cref="Intensity"/> and <see cref="Sharpness"/>
    /// are normalized 0..1; <see cref="StartTime"/> and <see cref="Duration"/> are seconds. Sharpness maps to iOS
    /// CoreHaptics sharpness (crisp↔dull); Android has no sharpness concept, so there only the intensity (→amplitude)
    /// and the timings are used.
    /// </summary>
    [Serializable]
    public class HapticEvent
    {
        public float Intensity;
        public float Sharpness;
        public float StartTime;
        public float Duration;
    }
}
