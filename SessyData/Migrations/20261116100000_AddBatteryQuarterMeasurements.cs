using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <summary>
    /// Adds BatteryQuarterMeasurements (power and SOC per battery per quarter).
    /// Hand-written migration; the target model lives in ModelContextModelSnapshot.
    /// </summary>
    [DbContext(typeof(ModelContext))]
    [Migration("20261116100000_AddBatteryQuarterMeasurements")]
    public partial class AddBatteryQuarterMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BatteryQuarterMeasurements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BatteryId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    PowerWatts = table.Column<double>(type: "REAL", nullable: false),
                    StateOfChargeWh = table.Column<double>(type: "REAL", nullable: false),
                    CapacityWh = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatteryQuarterMeasurements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BatteryQuarterMeasurements_Time_BatteryId",
                table: "BatteryQuarterMeasurements",
                columns: new[] { "Time", "BatteryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatteryQuarterMeasurements");
        }
    }
}
