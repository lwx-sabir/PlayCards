using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyRewardTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerDailyAdUnlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CycleKey = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AdTransactionId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Network = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    SpentOnNode = table.Column<int>(type: "int", nullable: true),
                    SpentAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerDailyAdUnlocks", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PlayerDailyClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CycleKey = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Node = table.Column<int>(type: "int", nullable: false),
                    ClaimedOnUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ClaimedOnLocalDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WasAdUnlock = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Granted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerDailyClaims", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PlayerDailyCycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CycleIndex = table.Column<int>(type: "int", nullable: false),
                    StartLocalDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TimeZoneId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerDailyCycles", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDailyAdUnlocks_AdTransactionId",
                table: "PlayerDailyAdUnlocks",
                column: "AdTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDailyAdUnlocks_UserId_CycleKey",
                table: "PlayerDailyAdUnlocks",
                columns: new[] { "UserId", "CycleKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDailyClaims_UserId_CycleKey",
                table: "PlayerDailyClaims",
                columns: new[] { "UserId", "CycleKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDailyClaims_UserId_CycleKey_Node",
                table: "PlayerDailyClaims",
                columns: new[] { "UserId", "CycleKey", "Node" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDailyCycles_UserId",
                table: "PlayerDailyCycles",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerDailyAdUnlocks");

            migrationBuilder.DropTable(
                name: "PlayerDailyClaims");

            migrationBuilder.DropTable(
                name: "PlayerDailyCycles");
        }
    }
}
