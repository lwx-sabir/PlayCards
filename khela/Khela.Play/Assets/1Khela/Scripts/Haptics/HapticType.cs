namespace PlayCard.Haptics
{
    /// <summary>
    /// Semantic feedback intents, named after the platform-standard haptics so call-sites read by MEANING —
    /// <c>Haptic.Play(HapticType.Success)</c> — instead of magic numbers. Each maps to a tuned
    /// (duration, intensity, sharpness) preset in <see cref="Haptic"/>: on iOS that drives a CoreHaptics event,
    /// on Android a <c>VibrationEffect</c> amplitude, on WebGL a plain vibrate of that duration.
    /// </summary>
    public enum HapticType
    {
        Selection,     // light crisp tick — list scroll, toggle, focus change
        Success,       // positive confirmation — purchase, reward claimed
        Warning,       // caution — invalid but recoverable
        Failure,       // negative — action rejected, lost
        LightImpact,   // small contact
        MediumImpact,  // standard contact — button press, place
        HeavyImpact,   // strong contact — land, big hit
        RigidImpact,   // short + very crisp
        SoftImpact,    // short + very dull
    }
}
