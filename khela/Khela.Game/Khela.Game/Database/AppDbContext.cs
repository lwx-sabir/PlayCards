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

        public DbSet<StorePurchase> StorePurchases { get; set; }   // every real-money store transaction — the store's money audit (docs/IAP_SPEC.md §4.1)
        public DbSet<StoreEvent> StoreEvents { get; set; }         // store webhooks / poll findings, idempotent on the store's event id

        public DbSet<DeviceRegistration> DeviceRegistrations { get; set; }

        public DbSet<GameHandHeader> GameHandHeaders { get; set; }

        public DbSet<GameHandParticipant> GameHandParticipants { get; set; }

        public DbSet<GameHandAction> GameHandActions { get; set; }

        public DbSet<GameHandSnapshot> GameHandSnapshots { get; set; }

        // --- Profiles + leaderboards (AddUserProfileAndLeaderboards migration) ---
        public DbSet<UserProfile> UserProfiles { get; set; }

        public DbSet<UserGameStats> UserGameStats { get; set; }

        public DbSet<PlayerDailyStat> PlayerDailyStats { get; set; }   // windowed (daily/weekly/monthly) leaderboard source

        public DbSet<StatusPointsLedger> StatusPointsLedgers { get; set; }   // monthly SP + spend buckets → VIP tier window (§3)
        public DbSet<LoyaltyRedemption> LoyaltyRedemptions { get; set; }   // Loyalty-store redemption ledger (idempotency + audit, §4)
        public DbSet<PlayerReward> PlayerRewards { get; set; }   // claimable reward inbox (level-up/daily/pass/…); credited only on claim
        public DbSet<PlayerDailyMission> PlayerDailyMissions { get; set; }   // per-player daily mission set + progress
        public DbSet<PlayerDailyMissionBundle> PlayerDailyMissionBundles { get; set; }   // complete-all bundle claim marker

        public DbSet<LeaderboardDefinition> LeaderboardDefinitions { get; set; }

        public DbSet<LeaderboardInstance> LeaderboardInstances { get; set; }

        public DbSet<LeaderboardArchiveEntry> LeaderboardArchiveEntries { get; set; }

        public DbSet<LeaderboardSeason> LeaderboardSeasons { get; set; }

        public DbSet<UserLinkedAccount> UserLinkedAccounts { get; set; }

        // --- Social + gifts (AddSocialAndGifts migration) ---
        public DbSet<Friendship> Friendships { get; set; }

        public DbSet<Gift> Gifts { get; set; }

        public DbSet<ChatMessage> ChatMessages { get; set; }

        // --- Reports (AddProfileBlurbsAndReports migration) ---
        public DbSet<Report> Reports { get; set; }

        // --- Cosmetics shop (AddCosmeticsShop migration; docs/AVATAR_SHOP_SPEC.md) ---
        public DbSet<CosmeticSku> CosmeticSkus { get; set; }
        public DbSet<UserCosmetic> UserCosmetics { get; set; }

        // --- Pass: free daily ladder + Golden subscription (AddPassTables migration; docs/PASS_SPEC.md) ---
        public DbSet<PlayerPassClaim> PlayerPassClaims { get; set; }             // the only record of pass progress
        public DbSet<PlayerPassEntitlement> PlayerPassEntitlements { get; set; } // subscription windows (append-only)
        public DbSet<PlayerPassAdUnlock> PlayerPassAdUnlocks { get; set; }       // verified rewarded-ad catch-up credits

        public DbSet<PlayerDailyCycle> PlayerDailyCycles { get; set; }           // which run of the daily ladder a player is on
        public DbSet<PlayerDailyClaim> PlayerDailyClaims { get; set; }           // the only record of daily-login progress
        public DbSet<PlayerDailyAdUnlock> PlayerDailyAdUnlocks { get; set; }     // verified rewarded-ad catch-up credits

        public DbSet<PlayerPiggyBank> PlayerPiggyBanks { get; set; }             // the chip pack that fills as you play
        public DbSet<PiggyBreak> PiggyBreaks { get; set; }                       // every purchased bank — the money audit
    }
}
