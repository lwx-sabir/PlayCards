using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddSeasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Seasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Index = table.Column<int>(type: "int", nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RolledAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    PlayersRolled = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Seasons", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Seasons_Index",
                table: "Seasons",
                column: "Index",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Seasons_Status",
                table: "Seasons",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Seasons");
        }
    }
}
