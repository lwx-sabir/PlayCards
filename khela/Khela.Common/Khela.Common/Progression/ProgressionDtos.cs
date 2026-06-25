using System;

namespace Khela.Common.Progression
{
    /// <summary>
    /// The player's live XP/level state for the profile bar. <see cref="Xp"/> is the INTO-LEVEL progress
    /// (0..<see cref="XpToNext"/>), so the bar fill = Xp / XpToNext. <see cref="DailyXpRemaining"/> is how
    /// much more XP can still be earned today before the daily cap (0 = capped out).
    /// </summary>
    public class ProgressionDto
    {
        /// <summary>Current level (the badge number).</summary>
        public int Level { get; set; }

        /// <summary>XP earned into the current level (carries the remainder forward on level-up).</summary>
        public long Xp { get; set; }

        /// <summary>XP required to go from the current level to the next (the bar denominator).</summary>
        public long XpToNext { get; set; }

        /// <summary>XP still earnable today before the daily cap.</summary>
        public long DailyXpRemaining { get; set; }
    }
}
