using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddPiggyBankWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpiredCount",
                table: "PlayerPiggyBanks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "PlayerPiggyBanks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastExpiredAmount",
                table: "PlayerPiggyBanks",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastExpiredAtUtc",
                table: "PlayerPiggyBanks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReadyAtUtc",
                table: "PlayerPiggyBanks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SeenAtUtc",
                table: "PlayerPiggyBanks",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiredCount",
                table: "PlayerPiggyBanks");

            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "PlayerPiggyBanks");

            migrationBuilder.DropColumn(
                name: "LastExpiredAmount",
                table: "PlayerPiggyBanks");

            migrationBuilder.DropColumn(
                name: "LastExpiredAtUtc",
                table: "PlayerPiggyBanks");

            migrationBuilder.DropColumn(
                name: "ReadyAtUtc",
                table: "PlayerPiggyBanks");

            migrationBuilder.DropColumn(
                name: "SeenAtUtc",
                table: "PlayerPiggyBanks");
        }
    }
}
