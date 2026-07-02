using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddVipStatusLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BadgeLitUntil",
                table: "UserProfiles",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DailySpFromWager",
                table: "UserProfiles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "DailySpResetAt",
                table: "UserProfiles",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HideVipBadge",
                table: "UserProfiles",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LifetimeStatusPoints",
                table: "UserProfiles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "StatusPointsLedger",
                columns: table => new
                {
                    StatusPointsLedgerId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PeriodStart = table.Column<DateTime>(type: "date", nullable: false),
                    Sp = table.Column<long>(type: "bigint", nullable: false),
                    SpendUsd = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusPointsLedger", x => x.StatusPointsLedgerId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_StatusPointsLedger_UserId_PeriodStart",
                table: "StatusPointsLedger",
                columns: new[] { "UserId", "PeriodStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatusPointsLedger");

            migrationBuilder.DropColumn(
                name: "BadgeLitUntil",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "DailySpFromWager",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "DailySpResetAt",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "HideVipBadge",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "LifetimeStatusPoints",
                table: "UserProfiles");
        }
    }
}
