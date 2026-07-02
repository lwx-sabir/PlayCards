using System.Collections.Generic;

namespace PlayCard.Game.Net
{
    /// <summary>One leaderboard row — client mirror of the server's LbEntryDto. Add fields here (and the server record)
    /// as rows surface more data (VIP tier, level, win-rate, …); existing binders ignore what they don't read.</summary>
    public sealed class LbEntryData
    {
        public int Rank { get; set; }
        public string UserId { get; set; }
        public string DisplayName { get; set; }
        public string AvatarId { get; set; }   // → avatar sprite
        public string Region { get; set; }     // ISO region → flag sprite
        public decimal Score { get; set; }     // the metric value (xp / biggest win / streak)
    }

    /// <summary>One leaderboard page + the caller's own rank — client mirror of LbPageDto. A board = (Game, Metric,
    /// Period, Scope); <see cref="Me"/> is the caller's row (may be outside the top <see cref="Entries"/>).</summary>
    public sealed class LbPageData
    {
        public string Game { get; set; }
        public string Metric { get; set; }
        public string Period { get; set; }
        public string Scope { get; set; }
        public List<LbEntryData> Entries { get; set; } = new List<LbEntryData>();
        public LbEntryData Me { get; set; }
    }

    /// <summary>A featured board (for /boards) — client mirror of LbBoardDto.</summary>
    public sealed class LbBoardData
    {
        public string Game { get; set; }
        public string Metric { get; set; }
        public string Period { get; set; }
        public string DisplayName { get; set; }
    }
}
