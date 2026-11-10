using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <summary>
    /// Adds FinancialForecastReplays: the weekly planner replay, previously financial-forecast.json
    /// next to the database. Hand-written migration; the target model lives in ModelContextModelSnapshot.
    /// </summary>
    [DbContext(typeof(ModelContext))]
    [Migration("20261110210000_AddFinancialForecastReplays")]
    public partial class AddFinancialForecastReplays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FinancialForecastReplays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CalculatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    From = table.Column<DateTime>(type: "TEXT", nullable: false),
                    To = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Netting = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImportKWh = table.Column<double>(type: "REAL", nullable: false),
                    ExportKWh = table.Column<double>(type: "REAL", nullable: false),
                    ImportCostEur = table.Column<double>(type: "REAL", nullable: false),
                    ExportRevenueEur = table.Column<double>(type: "REAL", nullable: false),
                    EnergyTaxEur = table.Column<double>(type: "REAL", nullable: false),
                    FixedCostsEur = table.Column<double>(type: "REAL", nullable: false),
                    TaxReductionEur = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialForecastReplays", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialForecastReplays_CalculatedAt",
                table: "FinancialForecastReplays",
                column: "CalculatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinancialForecastReplays");
        }
    }
}
