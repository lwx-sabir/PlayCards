using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddCosmeticIcons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "IconPng",
                table: "CosmeticSkus",
                type: "longblob",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IconUpdatedAt",
                table: "CosmeticSkus",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IconPng",
                table: "CosmeticSkus");

            migrationBuilder.DropColumn(
                name: "IconUpdatedAt",
                table: "CosmeticSkus");
        }
    }
}
