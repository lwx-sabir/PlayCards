using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerDailyStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerDailyStats",
                columns: table => new
                {
                    PlayerDailyStatId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    GameType = table.Column<int>(type: "int", nullable: false),
                    StatDate = table.Column<DateTime>(type: "date", nullable: false),
                    Region = table.Column<string>(type: "char(2)", maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Xp = table.Column<long>(type: "bigint", nullable: false),
                    GamesPlayed = table.Column<long>(type: "bigint", nullable: false),
                    GamesWon = table.Column<long>(type: "bigint", nullable: false),
                    Wagered = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    ChipsWon = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    NetProfit = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    BiggestSingleWin = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerDailyStats", x => x.PlayerDailyStatId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_UserGameStats_GameType_BiggestSingleWin",
                table: "UserGameStats",
                columns: new[] { "GameType", "BiggestSingleWin" });

            migrationBuilder.CreateIndex(
                name: "IX_UserGameStats_GameType_ExperienceEarned",
                table: "UserGameStats",
                columns: new[] { "GameType", "ExperienceEarned" });

            migrationBuilder.CreateIndex(
                name: "IX_UserGameStats_GameType_LongestWinStreak",
                table: "UserGameStats",
                columns: new[] { "GameType", "LongestWinStreak" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDailyStats_GameType_StatDate",
                table: "PlayerDailyStats",
                columns: new[] { "GameType", "StatDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDailyStats_StatDate",
                table: "PlayerDailyStats",
                column: "StatDate");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDailyStats_UserId_GameType_StatDate",
                table: "PlayerDailyStats",
                columns: new[] { "UserId", "GameType", "StatDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerDailyStats");

            migrationBuilder.DropIndex(
                name: "IX_UserGameStats_GameType_BiggestSingleWin",
                table: "UserGameStats");

            migrationBuilder.DropIndex(
                name: "IX_UserGameStats_GameType_ExperienceEarned",
                table: "UserGameStats");

            migrationBuilder.DropIndex(
                name: "IX_UserGameStats_GameType_LongestWinStreak",
                table: "UserGameStats");
        }
    }
}
