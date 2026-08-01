using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <summary>
    /// Tags every money movement and audit row with the OPERATOR (licensee brand) it belongs to — groundwork for
    /// licensing the engine to gambling houses, each of whom needs their ledger and round history isolated.
    ///
    /// Existing rows are backfilled with "khela" (Tenant.Default) rather than the empty string EF generates by
    /// default, so no historical money is left untagged and per-operator reporting sees a complete history.
    /// </summary>
    public partial class AddOperatorTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperatorId",
                table: "WalletTransactions",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "khela")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "OperatorId",
                table: "GameHandParticipants",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "khela")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "OperatorId",
                table: "GameHandHeaders",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "khela")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_OperatorId_CreatedAt",
                table: "WalletTransactions",
                columns: new[] { "OperatorId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletTransactions_OperatorId_CreatedAt",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "OperatorId",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "OperatorId",
                table: "GameHandParticipants");

            migrationBuilder.DropColumn(
                name: "OperatorId",
                table: "GameHandHeaders");
        }
    }
}
