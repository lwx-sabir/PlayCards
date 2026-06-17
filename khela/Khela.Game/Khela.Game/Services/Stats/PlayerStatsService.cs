using LbGameType = Khela.Common.Leaderboards.GameType;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Leaderboards;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Services.Stats
{
    /// <summary>Per-participant net result of one settled round, fed to the stats roll-up.</summary>
    public readonly record struct RoundResult(Guid UserId, decimal Wagered, decimal Net);

    public interface IPlayerStatsService
    {
        /// <summary>Roll a settled round's results into UserGameStats (per game) + UserProfile (cross-game).</summary>
        Task RecordRoundResultsAsync(LbGameType gameType, IReadOnlyList<RoundResult> results);
    }

    /// <summary>
    /// Updates the durable player stats after a round settles: per-game <see cref="UserGameStats"/> and the
    /// cross-game <see cref="UserProfile"/> aggregate (games/wins, wagered/won/net, biggest win, streaks,
    /// XP/level, last-played). These are the AllTime-leaderboard source + profile display. Runs separately
    /// from the money path (the wallet already settled) — a failure here never affects balances.
    /// Uses the LEADERBOARD GameType (distinct from the hand-ledger GameType — never cast between them).
    /// </summary>
    public sealed class PlayerStatsService : IPlayerStatsService
    {
        private const long XpPerRound = 10;
        private const long XpPerWin   = 10;
        private const long XpPerLevel = 1000;

        private readonly AppDbContext _db;
        private readonly ILeaderboardService _leaderboard;

        public PlayerStatsService(AppDbContext db, ILeaderboardService leaderboard)
        {
            _db = db;
            _leaderboard = leaderboard;
        }

        public async Task RecordRoundResultsAsync(LbGameType gameType, IReadOnlyList<RoundResult> results)
        {
            if (results == null || results.Count == 0) return;

            var now = DateTime.UtcNow;
            var lbPushes = new List<(Guid UserId, string Region, RoundMetrics Metrics)>();

            foreach (var r in results)
            {
                var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == r.UserId);
                var region = profile?.Region ?? "ZZ";

                var stats = await _db.UserGameStats
                    .FirstOrDefaultAsync(s => s.UserId == r.UserId && s.GameType == gameType);
                if (stats == null)
                {
                    stats = new UserGameStats { UserId = r.UserId, GameType = gameType, Region = region };
                    _db.UserGameStats.Add(stats);
                }

                bool win = r.Net > 0m, loss = r.Net < 0m;
                long xp = XpPerRound + (win ? XpPerWin : 0);

                // ---- per-game (UserGameStats) ----
                stats.GamesPlayed++;
                stats.RoundsPlayed++;
                if (win)
                {
                    stats.GamesWon++;
                    stats.RoundsWon++;
                    stats.CurrentWinStreak++;
                    if (stats.CurrentWinStreak > stats.LongestWinStreak) stats.LongestWinStreak = stats.CurrentWinStreak;
                }
                else if (loss)
                {
                    stats.CurrentWinStreak = 0;
                }
                stats.ChipsWon += win ? r.Net : 0m;          // winnings (monotonic)
                stats.TotalWagered += r.Wagered;
                stats.NetProfit += r.Net;                     // signed
                if (win && r.Net > stats.BiggestSingleWin) stats.BiggestSingleWin = r.Net;
                stats.ExperienceEarned += xp;
                stats.FirstPlayedAt ??= now;
                stats.LastPlayedAt = now;
                stats.Region = region;
                stats.UpdatedAt = now;

                // ---- cross-game (UserProfile) ----
                if (profile != null)
                {
                    profile.GamesPlayed++;
                    if (win)
                    {
                        profile.GamesWon++;
                        profile.CurrentWinStreak++;
                        if (profile.CurrentWinStreak > profile.LongestWinStreak) profile.LongestWinStreak = profile.CurrentWinStreak;
                        profile.CurrentLoseStreak = 0;
                    }
                    else if (loss)
                    {
                        profile.CurrentLoseStreak++;
                        if (profile.CurrentLoseStreak > profile.LongestLoseStreak) profile.LongestLoseStreak = profile.CurrentLoseStreak;
                        profile.CurrentWinStreak = 0;
                    }
                    profile.TotalWagered += r.Wagered;
                    profile.TotalWon += win ? r.Net : 0m;
                    profile.NetProfit += r.Net;
                    if (win && r.Net > profile.BiggestWin) profile.BiggestWin = r.Net;
                    profile.Experience += xp;
                    profile.LifetimeExperience += xp;
                    profile.Level = 1 + (int)(profile.LifetimeExperience / XpPerLevel);
                    profile.LastPlayedGameType = gameType;
                    profile.LastPlayedAt = now;
                    profile.UpdatedAt = now;
                }

                lbPushes.Add((r.UserId, region, new RoundMetrics(
                    ChipsWon: win ? r.Net : 0m,
                    NetProfit: r.Net,
                    TotalWagered: r.Wagered,
                    Experience: xp,
                    RoundsWon: win ? 1 : 0,
                    BiggestWin: win ? r.Net : 0m,
                    LongestWinStreak: stats.LongestWinStreak,
                    GamesPlayed: 1)));
            }

            await _db.SaveChangesAsync();

            // Push the same per-round deltas to the live leaderboard ZSETs (best-effort; stats already saved).
            try
            {
                foreach (var p in lbPushes)
                    await _leaderboard.RecordRoundAsync(p.UserId, gameType, p.Region, p.Metrics);
            }
            catch { /* leaderboard is a rebuildable index — never fail stats over it */ }
        }
    }
}
