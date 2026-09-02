using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddSplitPlannedPowerToPlannedQuarter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "PlannedChargePowerW",
                table: "PlannedQuarters",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "PlannedDischargePowerW",
                table: "PlannedQuarters",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            // Back-fill the split fields for rows written before this migration from the existing
            // signed PlannedPowerW (positive = charge, negative = discharge). Lossless: at write
            // time only one direction was ever non-zero per quarter, so no information is invented.
            migrationBuilder.Sql(
                "UPDATE \"PlannedQuarters\" SET " +
                "\"PlannedChargePowerW\" = CASE WHEN \"PlannedPowerW\" > 0 THEN \"PlannedPowerW\" ELSE 0 END, " +
                "\"PlannedDischargePowerW\" = CASE WHEN \"PlannedPowerW\" < 0 THEN -\"PlannedPowerW\" ELSE 0 END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlannedChargePowerW",
                table: "PlannedQuarters");

            migrationBuilder.DropColumn(
                name: "PlannedDischargePowerW",
                table: "PlannedQuarters");
        }
    }
}
