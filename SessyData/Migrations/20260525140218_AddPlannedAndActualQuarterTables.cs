using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddPlannedAndActualQuarterTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActualQuarters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActualMode = table.Column<string>(type: "TEXT", nullable: false),
                    ActualPowerW = table.Column<double>(type: "REAL", nullable: false),
                    ActualSocWh = table.Column<double>(type: "REAL", nullable: false),
                    CurtailmentMode = table.Column<string>(type: "TEXT", nullable: false),
                    StateMachineReason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActualQuarters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlannedQuarters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PlannedMode = table.Column<string>(type: "TEXT", nullable: false),
                    PlannedPowerW = table.Column<double>(type: "REAL", nullable: false),
                    PlannedChargeLeftWh = table.Column<double>(type: "REAL", nullable: false),
                    SellingPriceEurKWh = table.Column<double>(type: "REAL", nullable: false),
                    BuyingPriceEurKWh = table.Column<double>(type: "REAL", nullable: false),
                    SolarForecastW = table.Column<double>(type: "REAL", nullable: false),
                    ConsumptionForecastW = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannedQuarters", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActualQuarters");

            migrationBuilder.DropTable(
                name: "PlannedQuarters");
        }
    }
}
