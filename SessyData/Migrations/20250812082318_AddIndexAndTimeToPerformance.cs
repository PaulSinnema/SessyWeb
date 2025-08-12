using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexAndTimeToPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "Time",
                table: "Performance",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_SolarInverterData_Time",
                table: "SolarInverterData",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_SolarData_Time",
                table: "SolarData",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_SessyWebControl_Time",
                table: "SessyWebControl",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_SessyStatusHistory_Time",
                table: "SessyStatusHistory",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_Performance_Time",
                table: "Performance",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_EPEXPrices_Time",
                table: "EPEXPrices",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_EnergyHistory_Time",
                table: "EnergyHistory",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_Consumption_Time",
                table: "Consumption",
                column: "Time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SolarInverterData_Time",
                table: "SolarInverterData");

            migrationBuilder.DropIndex(
                name: "IX_SolarData_Time",
                table: "SolarData");

            migrationBuilder.DropIndex(
                name: "IX_SessyWebControl_Time",
                table: "SessyWebControl");

            migrationBuilder.DropIndex(
                name: "IX_SessyStatusHistory_Time",
                table: "SessyStatusHistory");

            migrationBuilder.DropIndex(
                name: "IX_Performance_Time",
                table: "Performance");

            migrationBuilder.DropIndex(
                name: "IX_EPEXPrices_Time",
                table: "EPEXPrices");

            migrationBuilder.DropIndex(
                name: "IX_EnergyHistory_Time",
                table: "EnergyHistory");

            migrationBuilder.DropIndex(
                name: "IX_Consumption_Time",
                table: "Consumption");

            migrationBuilder.DropColumn(
                name: "Time",
                table: "Performance");
        }
    }
}
