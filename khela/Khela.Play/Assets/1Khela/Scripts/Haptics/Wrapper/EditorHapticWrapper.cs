namespace PlayCard.Haptics
{
    /// <summary>Editor stub — logs (when verbose) instead of buzzing, since the editor has no vibration motor.</summary>
    public sealed class EditorHapticWrapper : BaseHapticWrapper
    {
        public override void Init() => Log("initialized (editor stub — no physical feedback).");

        public override void Play(float duration, float intensity, float sharpness)
            => Log($"Play(duration: {duration:0.###}, intensity: {intensity:0.##}, sharpness: {sharpness:0.##})");

        public override void Play(string patternId) => Log($"PlayPattern(id: {patternId})");

        public override void RegisterPattern(HapticPattern pattern) => Log($"RegisterPattern(id: {pattern.ID})");
    }
}
