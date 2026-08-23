using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Khela.Game.Migrations
{
    /// <summary>
    /// Carries every VIP level a player already has onto the new HELD snapshot (docs/VIP_SPEC.md §4).
    ///
    /// The level used to be ground from play; it is now the band of the VIP-P a player has BOUGHT. Without this, the
    /// first status read after the deploy would find a window of zero, no hold, and quietly demote every VIP in the
    /// game to nothing. So the level they hold becomes a hold — with a window to spend it, after which the new rule
    /// takes over. This is the same courtesy the season bootstrap gave the badges: nobody is demoted by the switch
    /// itself, only by the rule going forward.
    ///
    /// Data-only and idempotent by its WHERE clause (it can only ever raise VipHeldLevel to VipLevel).
    /// </summary>
    public partial class SeedVipHeldLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One statement, so the level and the window it gets are set together: the grace is a fixed 30 days from the
            // deploy rather than whatever half-lapsed maintenance window play happened to leave behind.
            migrationBuilder.Sql(@"
                UPDATE UserProfiles
                   SET VipHeldLevel = VipLevel,
                       VipLevelMaintainedThrough = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 30 DAY)
                 WHERE VipLevel > 0 AND VipHeldLevel = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The carried snapshot is indistinguishable from one a purchase wrote, so there is nothing safe to undo:
            // clearing it would demote players who have since PAID for the level they hold.
        }
    }
}
