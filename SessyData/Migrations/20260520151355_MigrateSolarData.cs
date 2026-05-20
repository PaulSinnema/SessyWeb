using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class MigrateSolarData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Copy all SolarInverterData records into InverterMeasurements.
            // SolarInverterData stores instantaneous power in Watts at 15-minute intervals.
            // Convert to kWh: Power (W) * 0.25h / 1000 = kWh per quarter.
            // Only insert records that don't already exist in InverterMeasurements.
            migrationBuilder.Sql(@"
                INSERT INTO InverterMeasurements (Time, InverterId, ProviderName, SolarProductionKWh)
                SELECT
                    s.Time,
                    s.InverterId,
                    s.ProviderName,
                    s.Power * 0.25 / 1000.0
                FROM SolarInverterData s
                WHERE NOT EXISTS (
                    SELECT 1 FROM InverterMeasurements i
                    WHERE i.Time = s.Time AND i.InverterId = s.InverterId
                );
            ");

            // Step 2: Update QuarterlyMeasurements.SolarProductionKWh from InverterMeasurements
            // for all records where they differ (including historical records now populated
            // from SolarInverterData above, and records previously using forecast values).
            migrationBuilder.Sql(@"
                UPDATE QuarterlyMeasurements
                SET SolarProductionKWh = (
                    SELECT SUM(i.SolarProductionKWh)
                    FROM InverterMeasurements i
                    WHERE i.Time = QuarterlyMeasurements.Time
                )
                WHERE EXISTS (
                    SELECT 1 FROM InverterMeasurements i
                    WHERE i.Time = QuarterlyMeasurements.Time
                    AND ABS(i.SolarProductionKWh - QuarterlyMeasurements.SolarProductionKWh) > 0.001
                );
            "); 
            
            migrationBuilder.DropTable(
                name: "SolarInverterData");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolarInverterData",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InverterId = table.Column<string>(type: "TEXT", nullable: true),
                    Power = table.Column<double>(type: "REAL", nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", nullable: true),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolarInverterData", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolarInverterData_Time",
                table: "SolarInverterData",
                column: "Time");

            migrationBuilder.Sql(@"
                INSERT INTO SolarInverterData (Time, Power, InverterId, ProviderName)
                SELECT Time, SolarProductionKWh * 1000.0 / 0.25, InverterId, ProviderName
                FROM InverterMeasurements;
            ");
        }
    }
}
