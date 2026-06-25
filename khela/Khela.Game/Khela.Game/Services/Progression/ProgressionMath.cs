using System;
using System.Collections.Generic;

namespace Khela.Game.Services.Progression
{
    /// <summary>Tunable progression knobs (Progression Spec System A), bound from the "Progression" config section.</summary>
    public sealed class ProgressionConfig
    {
        public bool Enabled { get; init; } = true;                  // master switch for the game-extension layer (gifted-taint + XP)
        public decimal XpChipsPerPoint { get; init; } = 10m;        // chips of clean wager per 1 XP
        public decimal MaxWagerPerBet { get; init; } = 0m;          // cap on XP-eligible wager per round (0 = uncapped)
        public decimal MinBetEarly { get; init; } = 1000m;          // full-XP floor at level <= EarlyMaxLevel
        public decimal MinBetLate { get; init; } = 5000m;           // full-XP floor above EarlyMaxLevel
        public int EarlyMaxLevel { get; init; } = 3;
        public decimal SubFloorXpMultiplier { get; init; } = 0.2m;  // XP multiplier for bets below the floor (not zero)
        public decimal WinXpBonus { get; init; } = 0.1m;            // extra XP fraction on a win
        public long DailyXpCap { get; init; } = 150_000;
        public long XpBase { get; init; } = 150;                    // curve coefficient
        public double XpExp { get; init; } = 1.6;                   // curve exponent (super-linear)
        public long LvlupBase { get; init; } = 10_000;              // chips per level on level-up
        public int MilestoneEveryLevels { get; init; } = 10;
    }

    /// <summary>
    /// Pure progression arithmetic (curve, XP accrual, level-up loop) — no DB, no clock — so it is fully
    /// unit-testable. Consumed by <c>ProgressionService</c>.
    /// </summary>
    public static class ProgressionMath
    {
        /// <summary>XP needed to go from <paramref name="level"/> to the next: super-linear, rounded to 50.</summary>
        public static long XpToNext(int level, long xpBase, double xpExp)
        {
            if (level < 1) level = 1;
            return (long)Math.Round(xpBase * Math.Pow(level, xpExp) / 50.0) * 50;
        }

        /// <summary>
        /// Raw XP for one round's EARNED (clean) wager at a given level, BEFORE the daily cap. Tiered soft
        /// floor: full rate at/above the level's floor (MinBetEarly at L≤EarlyMaxLevel, else MinBetLate),
        /// <see cref="ProgressionConfig.SubFloorXpMultiplier"/> below it; plus a small win bonus.
        /// </summary>
        public static long RawXp(decimal cleanWager, int level, bool win, ProgressionConfig c)
        {
            if (cleanWager <= 0m || c.XpChipsPerPoint <= 0m) return 0;
            if (level < 1) level = 1;
            var floorBet = level <= c.EarlyMaxLevel ? c.MinBetEarly : c.MinBetLate;
            var mult = cleanWager >= floorBet ? 1m : c.SubFloorXpMultiplier;   // floor test on the REAL wager
            // Cap the XP-eligible wager per round so one huge stake can't grant outsized XP (0 = uncapped).
            var eligible = c.MaxWagerPerBet > 0m ? Math.Min(cleanWager, c.MaxWagerPerBet) : cleanWager;
            var baseXp = (long)Math.Floor(Math.Floor(eligible / c.XpChipsPerPoint) * mult);
            var winBonus = win ? (long)Math.Floor(baseXp * c.WinXpBonus) : 0;
            return baseXp + winBonus;
        }

        /// <summary>
        /// Fold <paramref name="grantedXp"/> into the into-level counter, leveling up with carry-over (a large
        /// grant can cross several levels). Returns the new into-level XP, the new level, and every level
        /// crossed (so the caller can credit each level's reward).
        /// </summary>
        public static (long Experience, int Level, List<int> CrossedLevels) ApplyLevelUps(
            long experience, int level, long grantedXp, long xpBase, double xpExp)
        {
            if (level < 1) level = 1;
            experience += grantedXp;
            var crossed = new List<int>();
            long need;
            while (experience >= (need = XpToNext(level, xpBase, xpExp)) && need > 0)
            {
                experience -= need;
                level++;
                crossed.Add(level);
            }
            return (experience, level, crossed);
        }

        /// <summary>Chip reward granted on reaching <paramref name="level"/>, rounded to 100.</summary>
        public static long LevelUpReward(int level, long lvlupBase)
            => (long)Math.Round(lvlupBase * (double)level / 100.0) * 100;
    }
}
