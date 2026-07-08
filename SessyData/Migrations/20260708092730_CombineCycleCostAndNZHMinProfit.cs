using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class CombineCycleCostAndNZHMinProfit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CycleCost",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "NetZeroHomeMinProfit",
                table: "Settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CycleCost",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "NetZeroHomeMinProfit",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
