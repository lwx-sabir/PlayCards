using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Khela.Game.Database.Models;
using Khela.Game.Services.Chests;
using GameType = Khela.Common.Leaderboards.GameType;   // the LEADERBOARD GameType (matches UserGameStats), not the ledger enum

namespace Khela.Game.Services.Missions
{
    /// <summary>A trackable mission metric. Each maps to an event the settle roll-up already produces (round count,
    /// wager, hand outcomes from the stat counters), so progress is free — no new gameplay plumbing.</summary>
    public enum MissionType
    {
        PlayRounds,     // +1 per settled round
        WinHands,       // + hands won this round  (stat counter "handsWon")
        WagerChips,     // + clean wager this round
        GetBlackjacks,  // + blackjacks this round
        Doubles,        // + doubles this round
        Splits,         // + splits this round
        Pushes,         // + pushes this round
    }

    public enum MissionDifficulty { Easy = 0, Medium = 1, Hard = 2 }

    /// <summary>A mission DEFINITION (the pool). Mutable + JSON-friendly so it round-trips through the admin editor.</summary>
    public sealed class MissionDef
    {
        public string Id { get; set; }
        public GameType Game { get; set; }
        public MissionType Type { get; set; }
        public long Target { get; set; }
        public MissionDifficulty Difficulty { get; set; }
        public CurrencyType RewardCurrency { get; set; } = CurrencyType.Chips;
        public decimal RewardAmount { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string IconKey { get; set; }
    }

    /// <summary>The reward for completing ALL daily missions — a chest, rolled + credited on claim (see ChestService).</summary>
    public sealed class BundleChest
    {
        public string Key { get; set; } = "CK_Chest";          // which chest (ChestDef.Key) the complete-all grants
        public ChestTier Tier { get; set; } = ChestTier.Common;
    }

    /// <summary>
    /// The full mission config (the overlay shape): the pool + how many of each difficulty to assign per day + the
    /// complete-all bundle. Admin-editable as JSON in the Redis key <c>khela:missions</c>; falls back to
    /// <see cref="MissionCatalog.Defaults"/>. Adding a mission = a new entry here; a brand-new metric also needs a
    /// <see cref="MissionType"/> value + a delta in <c>MissionService.ReportRoundAsync</c>.
    /// </summary>
    public sealed class MissionConfig
    {
        public List<MissionDef> Missions { get; set; } = new List<MissionDef>();
        public int DailyEasy { get; set; } = 2;
        public int DailyMedium { get; set; } = 2;
        public int DailyHard { get; set; } = 1;
        public BundleChest Bundle { get; set; } = new BundleChest();

        public MissionDef Find(string id) => string.IsNullOrEmpty(id) ? null : Missions?.FirstOrDefault(m => m.Id == id);

        public List<MissionDef> Pool(MissionDifficulty difficulty, IReadOnlyCollection<GameType> games)
            => (Missions ?? new List<MissionDef>())
               .Where(m => m.Difficulty == difficulty && (games == null || games.Contains(m.Game))).ToList();

        public int DailyCount(MissionDifficulty d)
            => d == MissionDifficulty.Easy ? DailyEasy : d == MissionDifficulty.Medium ? DailyMedium : DailyHard;
    }

    public static class MissionCatalog
    {
        /// <summary>The Redis key holding the admin-edited mission config JSON (overlay; absent ⇒ code defaults).</summary>
        public const string RedisKey = "khela:missions";

        /// <summary>String-enum + indented JSON so the admin editor is human-readable. Shared by the server reader
        /// and the dashboard writer so both round-trip identically.</summary>
        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>The built-in mission pool (used when no admin override exists). Rewards kept MODEST vs stakes.</summary>
        public static MissionConfig Defaults() => new MissionConfig
        {
            DailyEasy = 2, DailyMedium = 2, DailyHard = 1,
            Bundle = new BundleChest { Key = "CK_Chest", Tier = ChestTier.Common },
            Missions = new List<MissionDef>
            {
                // ---- Blackjack · Easy ----
                new MissionDef { Id = "bj_play5",    Game = GameType.Blackjack, Type = MissionType.PlayRounds,    Target = 5,      Difficulty = MissionDifficulty.Easy,   RewardAmount = 500m,  Title = "Warm Up",        Description = "Play 5 rounds",        IconKey = "blackjack_play" },
                new MissionDef { Id = "bj_win3",     Game = GameType.Blackjack, Type = MissionType.WinHands,      Target = 3,      Difficulty = MissionDifficulty.Easy,   RewardAmount = 500m,  Title = "First Wins",     Description = "Win 3 hands",          IconKey = "blackjack_win" },
                new MissionDef { Id = "bj_wager20",  Game = GameType.Blackjack, Type = MissionType.WagerChips,    Target = 20000,  Difficulty = MissionDifficulty.Easy,   RewardAmount = 500m,  Title = "High Roller I",  Description = "Wager 20,000 chips",   IconKey = "blackjack_wager" },

                // ---- Blackjack · Medium ----
                new MissionDef { Id = "bj_win8",     Game = GameType.Blackjack, Type = MissionType.WinHands,      Target = 8,      Difficulty = MissionDifficulty.Medium, RewardAmount = 1500m, Title = "On a Roll",      Description = "Win 8 hands",          IconKey = "blackjack_win" },
                new MissionDef { Id = "bj_bj2",      Game = GameType.Blackjack, Type = MissionType.GetBlackjacks, Target = 2,      Difficulty = MissionDifficulty.Medium, RewardAmount = 1500m, Title = "Natural",        Description = "Hit 2 blackjacks",     IconKey = "blackjack_natural" },
                new MissionDef { Id = "bj_dbl3",     Game = GameType.Blackjack, Type = MissionType.Doubles,       Target = 3,      Difficulty = MissionDifficulty.Medium, RewardAmount = 1500m, Title = "Down the Line",  Description = "Double down 3 times",  IconKey = "blackjack_double" },
                new MissionDef { Id = "bj_wager100", Game = GameType.Blackjack, Type = MissionType.WagerChips,    Target = 100000, Difficulty = MissionDifficulty.Medium, RewardAmount = 1500m, Title = "High Roller II", Description = "Wager 100,000 chips",  IconKey = "blackjack_wager" },

                // ---- Blackjack · Hard ----
                new MissionDef { Id = "bj_bj4",      Game = GameType.Blackjack, Type = MissionType.GetBlackjacks, Target = 4,      Difficulty = MissionDifficulty.Hard,   RewardAmount = 4000m, Title = "Blackjack King", Description = "Hit 4 blackjacks",     IconKey = "blackjack_natural" },
                new MissionDef { Id = "bj_win15",    Game = GameType.Blackjack, Type = MissionType.WinHands,      Target = 15,     Difficulty = MissionDifficulty.Hard,   RewardAmount = 4000m, Title = "Closer",         Description = "Win 15 hands",         IconKey = "blackjack_win" },
                new MissionDef { Id = "bj_split3",   Game = GameType.Blackjack, Type = MissionType.Splits,        Target = 3,      Difficulty = MissionDifficulty.Hard,   RewardAmount = 4000m, Title = "Split Decision", Description = "Split 3 times",        IconKey = "blackjack_split" },
            },
        };

        /// <summary>Parse an admin override JSON into a config; returns null if blank/invalid (caller falls back).</summary>
        public static MissionConfig TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var cfg = JsonSerializer.Deserialize<MissionConfig>(json, JsonOptions);
                return (cfg?.Missions != null && cfg.Missions.Count > 0) ? cfg : null;
            }
            catch { return null; }
        }
    }
}
