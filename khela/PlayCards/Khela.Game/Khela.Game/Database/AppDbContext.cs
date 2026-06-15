using Khela.Game.Database.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database
{
    public class AppDbContext : IdentityDbContext<ApplicationUser>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options): base(options)
        {
        } 

        public DbSet<PlayerWallet> PlayerWallets { get; set; }

        public DbSet<WalletTransaction> WalletTransactions { get; set; }

        public DbSet<StoreItem> StoreItems { get; set; }

        public DbSet<DeviceRegistration> DeviceRegistrations { get; set; }

        public DbSet<GameHandHeader> GameHandHeaders { get; set; }

        public DbSet<GameHandParticipant> GameHandParticipants { get; set; }

        public DbSet<GameHandAction> GameHandActions { get; set; }

        public DbSet<GameHandSnapshot> GameHandSnapshots { get; set; }

        // --- Profiles + leaderboards (AddUserProfileAndLeaderboards migration) ---
        public DbSet<UserProfile> UserProfiles { get; set; }

        public DbSet<UserGameStats> UserGameStats { get; set; }

        public DbSet<LeaderboardDefinition> LeaderboardDefinitions { get; set; }

        public DbSet<LeaderboardInstance> LeaderboardInstances { get; set; }

        public DbSet<LeaderboardArchiveEntry> LeaderboardArchiveEntries { get; set; }

        public DbSet<LeaderboardSeason> LeaderboardSeasons { get; set; }

        public DbSet<UserLinkedAccount> UserLinkedAccounts { get; set; }

        // --- Social + gifts (AddSocialAndGifts migration) ---
        public DbSet<Friendship> Friendships { get; set; }

        public DbSet<Gift> Gifts { get; set; }

        public DbSet<ChatMessage> ChatMessages { get; set; }
    }
}
