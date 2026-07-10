using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class EfficiencyParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DischargingEfficiencyFactor",
                table: "Settings",
                newName: "ThrottleFallbackPct");

            migrationBuilder.RenameColumn(
                name: "ChargingEfficiencyFactor",
                table: "Settings",
                newName: "RoundTripEfficiencyFallbackPct");
        }   

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ThrottleFallbackPct",
                table: "Settings",
                newName: "DischargingEfficiencyFactor");

            migrationBuilder.RenameColumn(
                name: "RoundTripEfficiencyFallbackPct",
                table: "Settings",
                newName: "ChargingEfficiencyFactor");
        }
    }
}
