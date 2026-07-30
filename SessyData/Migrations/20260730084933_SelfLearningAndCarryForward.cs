using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class SelfLearningAndCarryForward : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CarryForwardEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLearnedAt",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastLearnedSummary",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ReplacementCostPercentile",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 25.0);

            migrationBuilder.AddColumn<int>(
                name: "ReplacementCostWindowDays",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "SelfLearningEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ForecastSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LeadHours = table.Column<int>(type: "INTEGER", nullable: false),
                    SolarForecastW = table.Column<double>(type: "REAL", nullable: false),
                    ConsumptionForecastW = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastSnapshots_Time",
                table: "ForecastSnapshots",
                column: "Time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForecastSnapshots");

            migrationBuilder.DropColumn(
                name: "CarryForwardEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LastLearnedAt",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LastLearnedSummary",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ReplacementCostPercentile",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ReplacementCostWindowDays",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SelfLearningEnabled",
                table: "Settings");
        }
    }
}
