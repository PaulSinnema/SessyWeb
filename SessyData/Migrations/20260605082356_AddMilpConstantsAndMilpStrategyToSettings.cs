using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddMilpConstantsAndMilpStrategyToSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CheapRefillToleranceEur",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ExportPremiumEur",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ReserveSafetyFactor",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "SelfUseLookAheadQuarters",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "SolarHeadroomSafetyFactor",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "Strategy",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheapRefillToleranceEur",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ExportPremiumEur",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ReserveSafetyFactor",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SelfUseLookAheadQuarters",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SolarHeadroomSafetyFactor",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "Strategy",
                table: "Settings");
        }
    }
}
