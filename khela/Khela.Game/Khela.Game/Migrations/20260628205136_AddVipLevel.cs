using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddVipLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VipLevel",
                table: "UserProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "VipLevelMaintainedThrough",
                table: "UserProfiles",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VipLevelProgress",
                table: "UserProfiles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VipLevel",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "VipLevelMaintainedThrough",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "VipLevelProgress",
                table: "UserProfiles");
        }
    }
}
