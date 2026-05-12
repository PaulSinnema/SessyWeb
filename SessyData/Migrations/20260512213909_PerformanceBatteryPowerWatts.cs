using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceBatteryPowerWatts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BatteryPowerWatts",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            // Backfill historical records using ChargeLeft deltas.
            // Power (W) = -(delta ChargeLeft * 4): multiply by 4 to convert Wh/quarter to Watts.
            // Negative = charging (energy into battery), positive = discharging (energy out of battery).
            // Only charging and discharging rows are updated; idle rows remain 0.0.
            migrationBuilder.Sql(@"
                UPDATE Performance
                SET BatteryPowerWatts = -(
                    (SELECT p2.ChargeLeft FROM Performance p2 WHERE p2.Id = Performance.Id) -
                    (SELECT p1.ChargeLeft FROM Performance p1 WHERE p1.Id = Performance.Id - 1)
                ) * 4.0
                WHERE Charging = 1 OR Discharging = 1;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BatteryPowerWatts",
                table: "Performance");
        }
    }
}