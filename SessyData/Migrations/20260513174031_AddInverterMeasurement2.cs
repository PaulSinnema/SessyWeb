using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddInverterMeasurement2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Performance");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Performance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatteryPowerWatts = table.Column<double>(type: "REAL", nullable: false),
                    BuyingPrice = table.Column<double>(type: "REAL", nullable: false),
                    ChargeLeft = table.Column<double>(type: "REAL", nullable: false),
                    ChargeLeftPercentage = table.Column<double>(type: "REAL", nullable: false),
                    ChargeNeeded = table.Column<double>(type: "REAL", nullable: false),
                    Charging = table.Column<bool>(type: "INTEGER", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Discharging = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayState = table.Column<string>(type: "TEXT", nullable: true),
                    EstimatedConsumptionPerQuarterHour = table.Column<double>(type: "REAL", nullable: false),
                    IsReliable = table.Column<bool>(type: "INTEGER", nullable: false),
                    MarketPrice = table.Column<double>(type: "REAL", nullable: false),
                    Profit = table.Column<double>(type: "REAL", nullable: false),
                    SellingPrice = table.Column<double>(type: "REAL", nullable: false),
                    SmoothedBuyingPrice = table.Column<double>(type: "REAL", nullable: false),
                    SmoothedSellingPrice = table.Column<double>(type: "REAL", nullable: false),
                    SmoothedSolarPower = table.Column<double>(type: "REAL", nullable: false),
                    SolarGlobalRadiation = table.Column<double>(type: "REAL", nullable: false),
                    SolarPowerPerQuarterHour = table.Column<double>(type: "REAL", nullable: false),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VisualizeInChart = table.Column<double>(type: "REAL", nullable: false),
                    ZeroNetHome = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Performance", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Performance_Time",
                table: "Performance",
                column: "Time");
        }
    }
}
