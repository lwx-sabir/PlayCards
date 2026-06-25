using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletGiftedTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "GiftedBalanceAfter",
                table: "WalletTransactions",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GiftedBalanceBefore",
                table: "WalletTransactions",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GiftedDelta",
                table: "WalletTransactions",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "GiftedBalance",
                table: "PlayerWallets",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GiftedBalanceAfter",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "GiftedBalanceBefore",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "GiftedDelta",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "GiftedBalance",
                table: "PlayerWallets");
        }
    }
}
