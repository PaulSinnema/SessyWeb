using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class CleanupCorruptDataInQuarteryMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Console.WriteLine("Cleaning up solar corrupt data");

            // Cleanup corrupt data.
            migrationBuilder.Sql(
                "DELETE FROM QuarterlyMeasurements WHERE SolarProductionKWh > 100;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
