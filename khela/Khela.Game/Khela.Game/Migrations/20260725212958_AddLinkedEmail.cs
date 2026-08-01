using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkedEmail",
                table: "AspNetUsers",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkedEmail",
                table: "AspNetUsers");
        }
    }
}
