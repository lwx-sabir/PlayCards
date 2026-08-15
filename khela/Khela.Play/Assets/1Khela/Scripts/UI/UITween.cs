namespace PlayCard.UI
{
    /// <summary>
    /// Tiny easing helpers for juicy UI motion. The "Back" eases briefly pass their endpoint to create an overshoot
    /// (in) / wind-up (out) bounce — pair them with <c>Vector2.LerpUnclamped</c> so the position can travel past the
    /// target and settle. <c>overshoot</c> is the single juice knob: 0 = clean cubic (no bounce), ~1.7 ≈ 10% overshoot,
    /// higher = bouncier.
    /// </summary>
    public static class UITween
    {
        /// <summary>Ease OUT with overshoot — accelerates in, sails slightly PAST the target, settles back. Use on SHOW.</summary>
        public static float EaseOutBack(float t, float overshoot)
        {
            t -= 1f;
            return t * t * ((overshoot + 1f) * t + overshoot) + 1f;
        }

        /// <summary>Ease IN with a wind-up — pulls slightly the WRONG way first, then shoots to the target. Use on HIDE.</summary>
        public static float EaseInBack(float t, float overshoot)
            => t * t * ((overshoot + 1f) * t - overshoot);

        /// <summary>
        /// Ease OUT, no overshoot — fast off the mark, decelerating into the target. The workhorse for anything that
        /// must feel INSTANT: most of the distance is covered in the first third of the duration, so the eye reads the
        /// motion as having already happened. Use it for button presses and any response to a touch.
        ///
        /// Prefer this over <see cref="EaseOutBack"/> when the travel is short. Back's overshoot is a fraction of the
        /// DISTANCE, so over a 6% scale change even a bouncy 1.7 overshoot lands under 1% — invisible. Short pops need
        /// an explicit overshoot target, not a Back ease.
        /// </summary>
        public static float EaseOutCubic(float t)
        {
            t = 1f - t;
            return 1f - t * t * t;
        }
    }
}
