namespace PlayCard.Haptics
{
    /// <summary>
    /// A single one-shot haptic: how long, how strong, how crisp. <see cref="Sharpness"/> is honoured on iOS
    /// (CoreHaptics) and ignored on Android/WebGL, which have no sharpness concept. <see cref="Intensity"/> is
    /// normalized 0..1 (mapped to amplitude 1..255 on Android). Unlike the source system, no preset uses a 0
    /// intensity — a "light" tap must still be felt.
    /// </summary>
    public class HapticData
    {
        public float Duration;
        public float Intensity;
        public float Sharpness;

        public HapticData(float duration, float intensity = 1f, float sharpness = 0f)
        {
            Duration = duration;
            Intensity = intensity;
            Sharpness = sharpness;
        }
    }
}
