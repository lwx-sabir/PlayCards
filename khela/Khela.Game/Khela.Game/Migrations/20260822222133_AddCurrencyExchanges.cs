using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrencyExchanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CurrencyExchanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RequestId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    PairKey = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FromCurrency = table.Column<int>(type: "int", nullable: false),
                    FromAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ToCurrency = table.Column<int>(type: "int", nullable: false),
                    ToAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RateFromPerUnit = table.Column<decimal>(type: "decimal(28,10)", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Error = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyExchanges", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyExchanges_CreatedAt",
                table: "CurrencyExchanges",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyExchanges_UserId_PairKey_CreatedAt",
                table: "CurrencyExchanges",
                columns: new[] { "UserId", "PairKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyExchanges_UserId_RequestId",
                table: "CurrencyExchanges",
                columns: new[] { "UserId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CurrencyExchanges");
        }
    }
}
