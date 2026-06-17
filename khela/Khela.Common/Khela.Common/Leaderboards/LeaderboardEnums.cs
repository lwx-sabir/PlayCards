namespace Khela.Common.Leaderboards
{
    /// <summary>
    /// Game vertical for leaderboards and per-game stats. 0 = General is the cross-game aggregate.
    /// DISTINCT from Khela.Game.Database.Models.GameType (the hand-ledger enum) — do not merge.
    /// Values are a persisted + Redis-key + client wire contract: only ever APPEND, never renumber.
    /// </summary>
    public enum GameType
    {
        General   = 0,  // cross-game aggregate board
        Blackjack = 1,
        Poker     = 2,  // collapses Holdem/Omaha into one ranking bucket
        TeenPatti = 3,
        Roulette  = 4
    }

    /// <summary>What a board ranks by. Not every metric is valid for every game (gated by config).</summary>
    public enum LeaderboardMetric
    {
        ChipsWon         = 0, // gross chips won
        NetProfit        = 1, // won - wagered (can be negative)
        Experience       = 2, // XP earned in the window
        RoundsWon        = 3, // hands/rounds won
        BiggestWin       = 4, // max single win in the window (MAX semantics)
        LongestWinStreak = 5, // max streak in the window (MAX semantics)
        TotalWagered     = 6,
        GamesPlayed      = 7
    }

    /// <summary>How a metric aggregates over its window. Drives the MySQL upsert and the Redis op.</summary>
    public enum MetricAggregation
    {
        Sum = 0, // running total   -> ZINCRBY
        Max = 1  // keep the greater -> ZADD GT
    }

    /// <summary>Geographic reach. Regional boards carry an ISO RegionKey; Global uses the sentinel "GLOBAL".</summary>
    public enum LeaderboardScope
    {
        Global   = 0,
        Regional = 1
    }

    /// <summary>Window cadence. The concrete window instance is the PeriodKey string.</summary>
    public enum LeaderboardPeriod
    {
        AllTime = 0,
        Season  = 1,
        Monthly = 2,
        Weekly  = 3,
        Daily   = 4
    }

    /// <summary>VIP/loyalty ladder. Shared so the Unity client renders the badge/frame.</summary>
    public enum VipTier
    {
        None     = 0,
        Bronze   = 1,
        Silver   = 2,
        Gold     = 3,
        Platinum = 4,
        Diamond  = 5,
        Elite    = 6
    }
}
