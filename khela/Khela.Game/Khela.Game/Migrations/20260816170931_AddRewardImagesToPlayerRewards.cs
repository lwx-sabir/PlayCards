using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddRewardImagesToPlayerRewards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImagesJson",
                table: "PlayerRewards",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImagesJson",
                table: "PlayerRewards");
        }
    }
}
