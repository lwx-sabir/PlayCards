namespace Khela.Game.Services.Rewards
{
    /// <summary>
    /// Cross-cutting switches for the reward ladders — the Monthly Pass and the Daily Login reward.
    ///
    /// Bound from the <c>Rewards</c> section of appsettings, with <c>reloadOnChange</c>, so flipping one on a running
    /// server takes effect on the next request. Read through <c>IOptionsMonitor</c>, never captured at construction.
    /// </summary>
    public sealed class RewardOptions
    {
        public const string Section = "Rewards";

        /// <summary>
        /// Hand missed days over for FREE instead of charging rewarded-ad views. Applies to EVERY ladder that has an
        /// ad catch-up — the pass and the daily login — deliberately, so a QA build can't have one of them still
        /// demanding ads while the other doesn't.
        ///
        /// This is a TESTING convenience: it lets the collect flow be exercised without waiting a day per claim, or
        /// on a build with no ad SDK wired. On a live server it is a faucet — every missed day of the cycle becomes
        /// free — so it must be off in production.
        ///
        /// It only ever makes a day EASIER to claim. It cannot unlock a day the calendar hasn't reached, and it never
        /// touches the golden track, which stays behind the subscription.
        /// </summary>
        public bool BypassAdForMissedDays { get; set; }
    }
}
