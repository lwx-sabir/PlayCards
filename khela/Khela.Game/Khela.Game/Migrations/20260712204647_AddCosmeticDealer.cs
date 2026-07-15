using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddCosmeticDealer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDealer",
                table: "CosmeticSkus",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDealer",
                table: "CosmeticSkus");
        }
    }
}
