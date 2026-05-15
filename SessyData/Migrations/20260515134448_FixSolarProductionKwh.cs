using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class FixSolarProductionKwh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Update SolarProductionKWh in QuarterlyMeasurements with the actual
            // measured values from InverterMeasurements. Previously this field was
            // filled with the planned/estimated value from the MILP QuarterlyInfo.
            migrationBuilder.Sql(@"
                UPDATE QuarterlyMeasurements
                SET SolarProductionKWh = (
                    SELECT SUM(im.SolarProductionKWh)
                    FROM InverterMeasurements im
                    WHERE im.Time = QuarterlyMeasurements.Time
                )
                WHERE EXISTS (
                    SELECT 1 FROM InverterMeasurements im
                    WHERE im.Time = QuarterlyMeasurements.Time
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Cannot reliably reverse — planned values are no longer available.
        }
    }
}
