using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class AddEnergyHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EnergyHistory",
                columns: table => new
                {
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedTariff1 = table.Column<double>(type: "REAL", nullable: false),
                    ConsumedTariff2 = table.Column<double>(type: "REAL", nullable: false),
                    ProducedTariff1 = table.Column<double>(type: "REAL", nullable: false),
                    ProducedTariff2 = table.Column<double>(type: "REAL", nullable: false),
                    TarrifIndicator = table.Column<int>(type: "INTEGER", nullable: false),
                    Temperature = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnergyHistory", x => x.Time);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnergyHistory");
        }
    }
}
