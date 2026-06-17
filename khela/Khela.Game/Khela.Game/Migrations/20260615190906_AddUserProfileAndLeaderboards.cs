using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfileAndLeaderboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeaderboardArchiveEntries",
                columns: table => new
                {
                    ArchiveEntryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    InstanceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    DefinitionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    DisplayName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AvatarId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AvatarFrameId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RegionKey = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    RewardGranted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RewardCorrelationId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RewardGrantedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    PeriodStartUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    SealedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaderboardArchiveEntries", x => x.ArchiveEntryId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeaderboardDefinitions",
                columns: table => new
                {
                    DefinitionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GameType = table.Column<int>(type: "int", nullable: false),
                    Metric = table.Column<int>(type: "int", nullable: false),
                    Aggregation = table.Column<int>(type: "int", nullable: false),
                    Period = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    HigherIsBetter = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SnapshotTopN = table.Column<int>(type: "int", nullable: false),
                    RedisRetentionHours = table.Column<int>(type: "int", nullable: false),
                    Archivable = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SeasonLengthDays = table.Column<int>(type: "int", nullable: true),
                    RewardTableId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaderboardDefinitions", x => x.DefinitionId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeaderboardInstances",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    DefinitionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PeriodKey = table.Column<string>(type: "varchar(48)", maxLength: 48, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RegionKey = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GameType = table.Column<int>(type: "int", nullable: false),
                    OpensAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ClosesAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SealedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaderboardInstances", x => x.InstanceId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LeaderboardSeasons",
                columns: table => new
                {
                    SeasonId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SeasonNumber = table.Column<int>(type: "int", nullable: false),
                    SeasonKey = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaderboardSeasons", x => x.SeasonId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "UserGameStats",
                columns: table => new
                {
                    UserGameStatsId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    GameType = table.Column<int>(type: "int", nullable: false),
                    Region = table.Column<string>(type: "char(2)", maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GamesPlayed = table.Column<long>(type: "bigint", nullable: false),
                    GamesWon = table.Column<long>(type: "bigint", nullable: false),
                    RoundsPlayed = table.Column<long>(type: "bigint", nullable: false),
                    RoundsWon = table.Column<long>(type: "bigint", nullable: false),
                    ChipsWon = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    TotalWagered = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    NetProfit = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    BiggestSingleWin = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CurrentWinStreak = table.Column<int>(type: "int", nullable: false),
                    LongestWinStreak = table.Column<int>(type: "int", nullable: false),
                    ExperienceEarned = table.Column<long>(type: "bigint", nullable: false),
                    IsFavorite = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    FirstPlayedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastPlayedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGameStats", x => x.UserGameStatsId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "UserLinkedAccounts",
                columns: table => new
                {
                    LinkedAccountId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ProviderUserId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Handle = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsLoginProvider = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsPublic = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLinkedAccounts", x => x.LinkedAccountId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    DisplayName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayNameNormalized = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AvatarId = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AvatarFrameId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CountryFlagId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Region = table.Column<string>(type: "char(2)", maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Experience = table.Column<long>(type: "bigint", nullable: false),
                    LifetimeExperience = table.Column<long>(type: "bigint", nullable: false),
                    VipTier = table.Column<int>(type: "int", nullable: false),
                    LoyaltyPoints = table.Column<long>(type: "bigint", nullable: false),
                    LifetimeLoyaltyPoints = table.Column<long>(type: "bigint", nullable: false),
                    GamesPlayed = table.Column<long>(type: "bigint", nullable: false),
                    GamesWon = table.Column<long>(type: "bigint", nullable: false),
                    TotalWagered = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    TotalWon = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    NetProfit = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    BiggestWin = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CurrentWinStreak = table.Column<int>(type: "int", nullable: false),
                    LongestWinStreak = table.Column<int>(type: "int", nullable: false),
                    CurrentLoseStreak = table.Column<int>(type: "int", nullable: false),
                    LongestLoseStreak = table.Column<int>(type: "int", nullable: false),
                    ReferralCount = table.Column<int>(type: "int", nullable: false),
                    FriendCount = table.Column<int>(type: "int", nullable: false),
                    DefaultGame = table.Column<int>(type: "int", nullable: true),
                    LastPlayedGameType = table.Column<int>(type: "int", nullable: true),
                    LastPlayedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.ProfileId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardArchiveEntries_InstanceId_Rank",
                table: "LeaderboardArchiveEntries",
                columns: new[] { "InstanceId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardArchiveEntries_RewardGranted_InstanceId",
                table: "LeaderboardArchiveEntries",
                columns: new[] { "RewardGranted", "InstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardArchiveEntries_UserId_InstanceId",
                table: "LeaderboardArchiveEntries",
                columns: new[] { "UserId", "InstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardDefinitions_Code",
                table: "LeaderboardDefinitions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardDefinitions_GameType_Metric_Period_Scope",
                table: "LeaderboardDefinitions",
                columns: new[] { "GameType", "Metric", "Period", "Scope" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardDefinitions_IsActive",
                table: "LeaderboardDefinitions",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardInstances_DefinitionId_PeriodKey_RegionKey",
                table: "LeaderboardInstances",
                columns: new[] { "DefinitionId", "PeriodKey", "RegionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardInstances_Status_ClosesAt",
                table: "LeaderboardInstances",
                columns: new[] { "Status", "ClosesAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardSeasons_IsActive",
                table: "LeaderboardSeasons",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardSeasons_SeasonKey",
                table: "LeaderboardSeasons",
                column: "SeasonKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaderboardSeasons_SeasonNumber",
                table: "LeaderboardSeasons",
                column: "SeasonNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserGameStats_GameType_ChipsWon",
                table: "UserGameStats",
                columns: new[] { "GameType", "ChipsWon" });

            migrationBuilder.CreateIndex(
                name: "IX_UserGameStats_GameType_NetProfit",
                table: "UserGameStats",
                columns: new[] { "GameType", "NetProfit" });

            migrationBuilder.CreateIndex(
                name: "IX_UserGameStats_UserId_GameType",
                table: "UserGameStats",
                columns: new[] { "UserId", "GameType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserLinkedAccounts_Provider_ProviderUserId",
                table: "UserLinkedAccounts",
                columns: new[] { "Provider", "ProviderUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserLinkedAccounts_UserId_Provider",
                table: "UserLinkedAccounts",
                columns: new[] { "UserId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_DisplayNameNormalized",
                table: "UserProfiles",
                column: "DisplayNameNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_Region",
                table: "UserProfiles",
                column: "Region");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_UserId",
                table: "UserProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_VipTier",
                table: "UserProfiles",
                column: "VipTier");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeaderboardArchiveEntries");

            migrationBuilder.DropTable(
                name: "LeaderboardDefinitions");

            migrationBuilder.DropTable(
                name: "LeaderboardInstances");

            migrationBuilder.DropTable(
                name: "LeaderboardSeasons");

            migrationBuilder.DropTable(
                name: "UserGameStats");

            migrationBuilder.DropTable(
                name: "UserLinkedAccounts");

            migrationBuilder.DropTable(
                name: "UserProfiles");
        }
    }
}
