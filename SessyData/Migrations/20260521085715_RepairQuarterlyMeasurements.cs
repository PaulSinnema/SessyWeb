using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class RepairQuarterlyMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove duplicate QuarterlyMeasurements — keep the record with the
            // highest Id (most recently written, so most likely to have correct data).
            migrationBuilder.Sql(@"
                DELETE FROM QuarterlyMeasurements
                WHERE Id NOT IN (
                    SELECT MAX(Id)
                    FROM QuarterlyMeasurements
                    GROUP BY Time
                );
            ");

            // Add unique index on Time to prevent future duplicates.
            migrationBuilder.CreateIndex(
                name: "IX_QuarterlyMeasurements_Time_Unique",
                table: "QuarterlyMeasurements",
                column: "Time",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QuarterlyMeasurements_Time_Unique",
                table: "QuarterlyMeasurements");
        }
    }
}
