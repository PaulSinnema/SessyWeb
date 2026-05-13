using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class RefactoringPerformanceTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuarterlyMeasurements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BatteryPowerWatts = table.Column<double>(type: "REAL", nullable: false),
                    BatteryStateOfChargeWh = table.Column<double>(type: "REAL", nullable: false),
                    BatteryMode = table.Column<int>(type: "INTEGER", nullable: false),
                    IsReliable = table.Column<bool>(type: "INTEGER", nullable: false),
                    SolarProductionKWh = table.Column<double>(type: "REAL", nullable: false),
                    GridImportWh = table.Column<double>(type: "REAL", nullable: false),
                    GridExportWh = table.Column<double>(type: "REAL", nullable: false),
                    BuyingPriceEur = table.Column<double>(type: "REAL", nullable: false),
                    SellingPriceEur = table.Column<double>(type: "REAL", nullable: false),
                    GlobalRadiation = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuarterlyMeasurements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuarterlyMeasurements_Time",
                table: "QuarterlyMeasurements",
                column: "Time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuarterlyMeasurements");
        }
    }
}
